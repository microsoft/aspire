// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Stores uploaded files from the Dashboard and maps file IDs to their temporary paths on disk.
/// </summary>
internal sealed class FileUploadStore : IFileUploadStore, IDisposable
{
    private static readonly TimeSpan s_cleanupInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, FileInteraction> _interactions = new();
    private readonly ITempFileSystemService _tempFileSystem;
    private readonly ILogger<FileUploadStore> _logger;
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly Task _cleanupTask;

    public FileUploadStore(IFileSystemService fileSystemService)
        : this(fileSystemService, NullLogger<FileUploadStore>.Instance)
    {
    }

    public FileUploadStore(IFileSystemService fileSystemService, ILogger<FileUploadStore> logger)
    {
        _tempFileSystem = fileSystemService.TempDirectory;
        _logger = logger;
        _cleanupTask = RunCleanupAsync(_cleanupCts.Token);
    }

    /// <summary>
    /// Registers an interaction that can own uploaded files.
    /// </summary>
    public void StartInteraction(int interactionId)
    {
        if (_interactions.TryAdd(interactionId, new FileInteraction()))
        {
            _logger.LogDebug("Started tracking file uploads for interaction {InteractionId}.", interactionId);
        }
    }

    /// <summary>
    /// Creates a new temp file path and returns the file ID and path.
    /// </summary>
    public (string FileId, string FilePath) CreateEntry(string originalFileName, int interactionId, string inputName)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction))
        {
            throw new InvalidOperationException($"Interaction '{interactionId}' is not accepting file uploads.");
        }

        lock (interaction)
        {
            if (interaction.State != FileInteractionState.InProgress)
            {
                throw new InvalidOperationException($"Interaction '{interactionId}' is not accepting file uploads.");
            }

            // Sanitize the file name to prevent path traversal attacks.
            // Strip directory components for both Unix (/) and Windows (\) separators
            // regardless of the current platform, since the name comes from a remote client.
            var lastSep = originalFileName.AsSpan().LastIndexOfAny('/', '\\');
            var safeName = lastSep >= 0 ? originalFileName[(lastSep + 1)..] : originalFileName;

            var tempFile = _tempFileSystem.CreateTempFile(string.IsNullOrEmpty(safeName) ? null : safeName);
            var fileId = Guid.NewGuid().ToString("N");

            _files[fileId] = new FileEntry(tempFile, interactionId, inputName);
            interaction.FileIds.Add(fileId);
            _logger.LogDebug(
                "Created uploaded file entry {FileId} for interaction {InteractionId}, input {InputName}, and file {FileName}.",
                fileId,
                interactionId,
                inputName,
                safeName);
            return (fileId, tempFile.Path);
        }
    }

    /// <summary>
    /// Marks a file upload as successfully completed.
    /// </summary>
    public void CompleteUpload(string fileId)
    {
        if (!_files.TryGetValue(fileId, out var entry))
        {
            return;
        }

        bool removeEntry;
        lock (entry)
        {
            entry.UploadComplete = true;
            removeEntry = entry.InteractionState == FileInteractionState.Canceled;
        }

        _logger.LogDebug(
            "Completed upload for file entry {FileId}, interaction {InteractionId}, and input {InputName}.",
            fileId,
            entry.InteractionId,
            entry.InputName);

        if (removeEntry)
        {
            RemoveEntry(fileId);
        }
    }

    /// <summary>
    /// Gets the file path for a given file ID and input name.
    /// </summary>
    public string? GetFilePath(string fileId, string inputName)
    {
        return _files.TryGetValue(fileId, out var entry) &&
            string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName)
                ? entry.TempFile.Path
                : null;
    }

    /// <summary>
    /// Gets the original file name for a given file ID.
    /// </summary>
    public string? GetFileName(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? Path.GetFileName(entry.TempFile.Path) : null;
    }

    /// <summary>
    /// Removes a file entry and deletes the associated file on disk.
    /// </summary>
    public void RemoveEntry(string fileId)
    {
        if (_files.TryRemove(fileId, out var entry))
        {
            _logger.LogDebug(
                "Removing uploaded file entry {FileId} for interaction {InteractionId} and input {InputName}.",
                fileId,
                entry.InteractionId,
                entry.InputName);

            if (_interactions.TryGetValue(entry.InteractionId, out var interaction))
            {
                lock (interaction)
                {
                    interaction.FileIds.Remove(fileId);
                }
            }

            try
            {
                entry.TempFile.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>
    /// Marks an interaction as completed and starts weak-reference tracking for its uploaded files.
    /// </summary>
    public void CompleteInteraction(int interactionId, IReadOnlyList<InteractionFile> files)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction))
        {
            return;
        }

        var filesById = files.ToLookup(file => file.Id, StringComparer.Ordinal);
        string[] fileIds;

        lock (interaction)
        {
            interaction.State = FileInteractionState.Complete;
            fileIds = [.. interaction.FileIds];
        }
        _interactions.TryRemove(KeyValuePair.Create(interactionId, interaction));

        _logger.LogDebug(
            "Completed file upload tracking for interaction {InteractionId} with {FileCount} uploaded files and {ReferenceCount} file references.",
            interactionId,
            fileIds.Length,
            files.Count);

        foreach (var fileId in fileIds)
        {
            if (!_files.TryGetValue(fileId, out var entry))
            {
                continue;
            }
            lock (entry)
            {
                entry.InteractionState = FileInteractionState.Complete;
                entry.References = filesById[fileId]
                    .Select(file => new WeakReference<InteractionFile>(file))
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Cancels an interaction and removes uploads that are no longer in progress.
    /// </summary>
    public void CancelInteraction(int interactionId)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction))
        {
            return;
        }

        string[] fileIds;
        lock (interaction)
        {
            interaction.State = FileInteractionState.Canceled;
            fileIds = [.. interaction.FileIds];
        }
        _interactions.TryRemove(KeyValuePair.Create(interactionId, interaction));

        _logger.LogDebug(
            "Canceled file upload tracking for interaction {InteractionId} with {FileCount} uploaded files.",
            interactionId,
            fileIds.Length);

        foreach (var fileId in fileIds)
        {
            if (!_files.TryGetValue(fileId, out var entry))
            {
                continue;
            }
            bool removeEntry;
            lock (entry)
            {
                entry.InteractionState = FileInteractionState.Canceled;
                removeEntry = entry.UploadComplete;
            }

            if (removeEntry)
            {
                RemoveEntry(fileId);
            }
        }
    }

    internal void RemoveUnreferencedFiles()
    {
        foreach (var (fileId, entry) in _files)
        {
            bool removeEntry;
            lock (entry)
            {
                removeEntry = entry.UploadComplete &&
                    (entry.InteractionState == FileInteractionState.Canceled ||
                     entry.InteractionState == FileInteractionState.Complete &&
                     (entry.References is null || entry.References.All(reference => !reference.TryGetTarget(out _))));
            }

            if (removeEntry)
            {
                RemoveEntry(fileId);
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_cleanupInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    RemoveUnreferencedFiles();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up unreferenced uploaded files. Cleanup will be retried.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Resolves a JSON-encoded file reference array into InputFileDto entries.
    /// Returns null if the value is empty, malformed, or contains no resolvable files.
    /// </summary>
    public static IReadOnlyList<InputFileDto>? ResolveFileReferences(IFileUploadStore store, string? jsonValue, string inputName, ILogger logger)
    {
        if (string.IsNullOrEmpty(jsonValue))
        {
            return null;
        }

        FileReference[]? fileRefs;
        try
        {
            fileRefs = JsonSerializer.Deserialize<FileReference[]>(jsonValue);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize file references for interaction input '{InputName}'. Treating as empty.", inputName);
            return null;
        }

        if (fileRefs is not { Length: > 0 })
        {
            return null;
        }

        var files = new List<InputFileDto>(fileRefs.Length);
        for (var idx = 0; idx < fileRefs.Length; idx++)
        {
            var fileRef = fileRefs[idx];
            var filePath = store.GetFilePath(fileRef.Id, inputName);
            if (filePath is null)
            {
                // Unknown file ID — skip to prevent using client-supplied IDs as arbitrary file paths.
                logger.LogWarning("Received unknown file ID '{FileId}' in interaction input '{InputName}'. Skipping.", fileRef.Id, inputName);
                continue;
            }
            var fileName = string.IsNullOrEmpty(fileRef.Name) ? store.GetFileName(fileRef.Id) ?? "" : fileRef.Name;
            files.Add(new InputFileDto(fileRef.Id, fileName, filePath));
        }

        return files.Count > 0 ? files : null;
    }

    public void Dispose()
    {
        _cleanupCts.Cancel();
        _cleanupTask.GetAwaiter().GetResult();
        _cleanupCts.Dispose();

        _logger.LogDebug(
            "Disposing file upload store with {FileCount} uploaded files and {InteractionCount} active interactions.",
            _files.Count,
            _interactions.Count);

        foreach (var entry in _files.Values)
        {
            try
            {
                entry.TempFile.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
        _files.Clear();
        _interactions.Clear();
    }

    // Shared type used by ResolveFileReferences for JSON deserialization of file input values.
    // The shape matches what the Dashboard sends: [{"Id":"...","Name":"..."}]
    private sealed class FileReference
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class FileEntry(TempFile tempFile, int interactionId, string inputName)
    {
        public TempFile TempFile { get; } = tempFile;
        public int InteractionId { get; } = interactionId;
        public string InputName { get; } = inputName;
        public bool UploadComplete { get; set; }
        public FileInteractionState InteractionState { get; set; }
        public IReadOnlyList<WeakReference<InteractionFile>>? References { get; set; }
    }

    private sealed class FileInteraction
    {
        public HashSet<string> FileIds { get; } = new(StringComparer.Ordinal);
        public FileInteractionState State { get; set; }
    }

    private enum FileInteractionState
    {
        InProgress,
        Complete,
        Canceled
    }
}
