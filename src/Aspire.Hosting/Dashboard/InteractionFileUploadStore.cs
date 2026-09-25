// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
            removeEntry = entry.State == FileEntryState.DiscardWhenComplete;
            if (entry.State == FileEntryState.Uploading)
            {
                entry.State = FileEntryState.Uploaded;
            }
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
    /// Gets the completed uploads for an interaction input.
    /// </summary>
    public IReadOnlyList<InteractionFileUpload> GetCompletedFiles(int interactionId, string inputName)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction))
        {
            return [];
        }

        var files = new List<InteractionFileUpload>();
        foreach (var (fileId, entry) in interaction.Files)
        {
            lock (entry)
            {
                if (entry.State is FileEntryState.Uploaded or FileEntryState.Accepted &&
                    string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName))
                {
                    files.Add(new InteractionFileUpload(fileId, entry.OriginalFileName, entry.TempFile.Path));
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Marks validated uploads as accepted into the interaction result.
    /// </summary>
    public void MarkFilesAccepted(int interactionId, string inputName, IReadOnlyList<string> fileIds)
    {
        foreach (var fileId in fileIds)
        {
            if (!TryGetEntry(interactionId, fileId, out var entry))
            {
                throw CreateFileMismatchException(inputName);
            }

            lock (entry)
            {
                if (entry.State is not (FileEntryState.Uploaded or FileEntryState.Accepted) ||
                    !string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName))
                {
                    throw CreateFileMismatchException(inputName);
                }

                entry.State = FileEntryState.Accepted;
            }
        }
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

        // Accepted entries belong to the result and remain available for caller disposal. An entry that became
        // Uploaded after the completed-file snapshot was taken was not accepted and is removed. Uploads still being
        // written are marked for deletion after their writer closes the file handle.
        foreach (var (fileId, entry) in interaction.Files)
        {
            bool removeEntry;
            lock (entry)
            {
                removeEntry = entry.State == FileEntryState.Uploaded;
                entry.State = entry.State switch
                {
                    FileEntryState.Uploading => FileEntryState.DiscardWhenComplete,
                    _ => entry.State
                };
            }

            if (removeEntry)
            {
                RemoveEntry(interactionId, fileId);
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
                removeEntry = entry.State is FileEntryState.Uploaded or FileEntryState.Accepted;
                entry.State = FileEntryState.DiscardWhenComplete;
            }

            if (removeEntry)
            {
                RemoveEntry(interactionId, fileId);
            }
        }

        RemoveInteractionIfEmpty(interactionId, interaction);
    }

    /// <summary>
    /// Validates that client-submitted file IDs exactly match the completed files and returns them in client order.
    /// </summary>
    public static IReadOnlyList<InteractionFileUpload>? ValidateFileReferences(string? jsonValue, string inputName, IReadOnlyList<InteractionFileUpload>? files)
    {
        // Clients submit file selections as: [{"Id":"<upload-id>","Name":"<display-name>"}].
        // Only Id participates in validation; Name is untrusted and resolved metadata remains authoritative.
        FileReference?[] fileReferences;
        try
        {
            fileReferences = string.IsNullOrEmpty(jsonValue)
                ? []
                : JsonSerializer.Deserialize<FileReference?[]>(jsonValue) ?? throw CreateFileMismatchException(inputName);
        }
        catch (JsonException ex)
        {
            throw CreateFileMismatchException(inputName, ex);
        }

        var filesById = (files ?? []).ToDictionary(file => file.Id, StringComparer.Ordinal);
        if (fileReferences.Length != filesById.Count)
        {
            throw CreateFileMismatchException(inputName);
        }

        var orderedFiles = new InteractionFileUpload[fileReferences.Length];
        for (var i = 0; i < fileReferences.Length; i++)
        {
            if (fileReferences[i] is not { Id.Length: > 0 } fileReference)
            {
                throw CreateFileMismatchException(inputName);
            }

            if (!filesById.Remove(fileReference.Id, out var file))
            {
                throw CreateFileMismatchException(inputName);
            }

            orderedFiles[i] = file;
        }

        return orderedFiles.Length > 0 ? orderedFiles : null;
    }

    private static InvalidOperationException CreateFileMismatchException(string inputName, Exception? innerException = null)
    {
        return new InvalidOperationException($"Submitted files for input '{inputName}' do not match the completed uploads.", innerException);
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

    private sealed class FileReference
    {
        public string? Id { get; set; }
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
        public FileEntryState State { get; set; }
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

    private enum FileEntryState
    {
        Uploading,
        Uploaded,
        Accepted,
        DiscardWhenComplete
    }
}
