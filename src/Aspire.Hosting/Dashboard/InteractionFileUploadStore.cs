// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Stores uploaded files from the Dashboard and maps file IDs to their temporary paths on disk.
/// </summary>
internal sealed class InteractionFileUploadStore : IInteractionFileUploadStore, IDisposable
{
    private readonly ConcurrentDictionary<int, FileInteraction> _interactions = new();
    private readonly ITempFileSystemService _tempFileSystem;
    private readonly ILogger<InteractionFileUploadStore> _logger;
    private int _disposed;

    public InteractionFileUploadStore(IFileSystemService fileSystemService, ILogger<InteractionFileUploadStore> logger)
    {
        _tempFileSystem = fileSystemService.TempDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Registers an interaction and the file inputs that can own uploaded files.
    /// </summary>
    public void StartInteraction(int interactionId, IReadOnlyList<(string InputName, int MaxFileCount)> fileInputs)
    {
        if (_interactions.TryAdd(interactionId, new FileInteraction(fileInputs)))
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

            if (!interaction.FileInputLimits.TryGetValue(inputName, out var maxFileCount))
            {
                throw new InvalidOperationException($"Interaction '{interactionId}' is not accepting file uploads for input '{inputName}'.");
            }

            // Each client submits one file selection per input during an interaction. Multi-file selections upload
            // their files sequentially as part of that single selection, so every upload counts toward this limit.
            // Count uploads in progress as reserved slots so concurrent requests cannot exceed the input's limit.
            var fileCount = interaction.Files.Values.Count(entry => string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName));
            if (fileCount >= maxFileCount)
            {
                var fileLabel = maxFileCount == 1 ? "file" : "files";
                throw new InvalidOperationException($"File input '{inputName}' accepts at most {maxFileCount} {fileLabel}.");
            }

            // Keep only the leaf name as metadata. The client-supplied name is never used for the
            // on-disk path because filename rules vary by platform and some names have special semantics.
            var lastSep = originalFileName.AsSpan().LastIndexOfAny('/', '\\');
            var safeName = lastSep >= 0 ? originalFileName[(lastSep + 1)..] : originalFileName;

            var tempFile = _tempFileSystem.CreateTempFile();
            var fileId = Guid.NewGuid().ToString("N");

            interaction.Files[fileId] = new FileEntry(tempFile, inputName, safeName);
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
    public void CompleteUpload(int interactionId, string fileId)
    {
        if (!TryGetEntry(interactionId, fileId, out var entry))
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
            interactionId,
            entry.InputName);

        if (removeEntry)
        {
            RemoveEntry(interactionId, fileId);
        }
    }

    /// <summary>
    /// Gets the file path for a given file ID, interaction ID, and input name.
    /// </summary>
    public string? GetFilePath(string fileId, int interactionId, string inputName)
    {
        if (!TryGetEntry(interactionId, fileId, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            return entry.UploadComplete &&
                string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName)
                    ? entry.TempFile.Path
                    : null;
        }
    }

    /// <summary>
    /// Gets the original file name for a given file ID.
    /// </summary>
    public string? GetFileName(int interactionId, string fileId)
    {
        return TryGetEntry(interactionId, fileId, out var entry) ? entry.OriginalFileName : null;
    }

    /// <summary>
    /// Removes a file entry and deletes the associated file on disk.
    /// </summary>
    public void RemoveEntry(int interactionId, string fileId)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction) ||
            !interaction.Files.TryRemove(fileId, out var entry))
        {
            return;
        }

        entry.TempFile.Dispose();

        _logger.LogDebug(
            "Removed uploaded file entry {FileId} for interaction {InteractionId} and input {InputName}.",
            fileId,
            interactionId,
            entry.InputName);

        RemoveInteractionIfEmpty(interactionId, interaction);
    }

    /// <summary>
    /// Marks an interaction as completed.
    /// </summary>
    public void CompleteInteraction(int interactionId)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction))
        {
            return;
        }

        lock (interaction)
        {
            interaction.State = FileInteractionState.Complete;
        }

        _logger.LogDebug(
            "Completed file upload tracking for interaction {InteractionId} with {FileCount} uploaded files.",
            interactionId,
            interaction.Files.Count);

        // The interaction completes before the prompt task returns, so its uploads must remain available for the
        // caller to process. The returned InteractionFileCollection owns their cleanup: .NET callers dispose the
        // collection from GetFiles(), and ATS callers invoke ReleaseFiles() on the original input builder. Store
        // disposal at AppHost shutdown is only a fallback for callers that do not release their files earlier.
        foreach (var entry in interaction.Files.Values)
        {
            lock (entry)
            {
                entry.InteractionState = FileInteractionState.Complete;
            }
        }

        RemoveInteractionIfEmpty(interactionId, interaction);
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

        lock (interaction)
        {
            interaction.State = FileInteractionState.Canceled;
        }

        _logger.LogDebug(
            "Canceled file upload tracking for interaction {InteractionId} with {FileCount} uploaded files.",
            interactionId,
            interaction.Files.Count);

        foreach (var (fileId, entry) in interaction.Files)
        {
            bool removeEntry;
            lock (entry)
            {
                entry.InteractionState = FileInteractionState.Canceled;
                removeEntry = entry.UploadComplete;
            }

            if (removeEntry)
            {
                RemoveEntry(interactionId, fileId);
            }
        }

        RemoveInteractionIfEmpty(interactionId, interaction);
    }

    /// <summary>
    /// Resolves a JSON-encoded file reference array into InputFileDto entries.
    /// Returns null if the value is empty, malformed, or contains no resolvable files.
    /// </summary>
    public static IReadOnlyList<InputFileDto>? ResolveFileReferences(IInteractionFileUploadStore store, string? jsonValue, int interactionId, string inputName, ILogger logger)
    {
        if (string.IsNullOrEmpty(jsonValue))
        {
            return null;
        }

        FileReference?[]? fileRefs;
        try
        {
            fileRefs = JsonSerializer.Deserialize<FileReference?[]>(jsonValue);
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
            if (fileRef is null || string.IsNullOrEmpty(fileRef.Id))
            {
                logger.LogWarning("Received malformed file reference in interaction input '{InputName}'. Skipping.", inputName);
                continue;
            }

            var filePath = store.GetFilePath(fileRef.Id, interactionId, inputName);
            if (filePath is null)
            {
                // Unknown file ID — skip to prevent using client-supplied IDs as arbitrary file paths.
                logger.LogWarning("Received unknown file ID '{FileId}' in interaction input '{InputName}'. Skipping.", fileRef.Id, inputName);
                continue;
            }
            var fileName = store.GetFileName(interactionId, fileRef.Id) ?? "";
            files.Add(new InputFileDto(fileRef.Id, fileName, filePath, () => store.RemoveEntry(interactionId, fileRef.Id)));
        }

        return files.Count > 0 ? files : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logger.LogDebug(
            "Disposing file upload store with {FileCount} uploaded files and {InteractionCount} tracked interactions.",
            _interactions.Values.Sum(interaction => interaction.Files.Count),
            _interactions.Count);

        foreach (var entry in _interactions.Values.SelectMany(interaction => interaction.Files.Values))
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
        _interactions.Clear();
    }

    // Shared type used by ResolveFileReferences for JSON deserialization of file input values.
    // The shape matches what the Dashboard sends: [{"Id":"...","Name":"..."}]
    private sealed class FileReference
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private bool TryGetEntry(int interactionId, string fileId, [NotNullWhen(true)] out FileEntry? entry)
    {
        entry = null;
        return _interactions.TryGetValue(interactionId, out var interaction) &&
            interaction.Files.TryGetValue(fileId, out entry);
    }

    private void RemoveInteractionIfEmpty(int interactionId, FileInteraction interaction)
    {
        lock (interaction)
        {
            if (interaction.State != FileInteractionState.InProgress && interaction.Files.IsEmpty)
            {
                _interactions.TryRemove(KeyValuePair.Create(interactionId, interaction));
            }
        }
    }

    private sealed class FileEntry(TempFile tempFile, string inputName, string originalFileName)
    {
        public TempFile TempFile { get; } = tempFile;
        public string InputName { get; } = inputName;
        public string OriginalFileName { get; } = originalFileName;
        public bool UploadComplete { get; set; }
        public FileInteractionState InteractionState { get; set; }
    }

    private sealed class FileInteraction(IReadOnlyList<(string InputName, int MaxFileCount)> fileInputs)
    {
        public ConcurrentDictionary<string, FileEntry> Files { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> FileInputLimits { get; } = fileInputs.ToDictionary(
            fileInput => fileInput.InputName,
            fileInput => fileInput.MaxFileCount,
            StringComparers.InteractionInputName);
        public FileInteractionState State { get; set; }
    }

    private enum FileInteractionState
    {
        InProgress,
        Complete,
        Canceled
    }
}
