// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.Utils;

/// <summary>
/// An in-memory implementation of <see cref="IInteractionFileUploadStore"/> for tests.
/// Does not write to disk or implement IDisposable.
/// </summary>
internal sealed class TestInteractionFileUploadStore : IInteractionFileUploadStore
{
    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.Ordinal);

    public ConcurrentQueue<int> StartedInteractions { get; } = new();
    public ConcurrentQueue<IReadOnlyList<(string InputName, int MaxFileCount)>> StartedFileInputs { get; } = new();
    public ConcurrentQueue<int> CompletedInteractions { get; } = new();
    public ConcurrentQueue<int> CanceledInteractions { get; } = new();
    public Action<int>? CompleteInteractionCallback { get; set; }
    public Action<int>? CancelInteractionCallback { get; set; }

    public void StartInteraction(int interactionId, IReadOnlyList<(string InputName, int MaxFileCount)> fileInputs)
    {
        StartedInteractions.Enqueue(interactionId);
        StartedFileInputs.Enqueue(fileInputs.ToArray());
    }

    public (string FileId, string FilePath) CreateEntry(string originalFileName, int interactionId, string inputName)
    {
        var fileId = Guid.NewGuid().ToString("N");
        // Use a synthetic path that won't conflict with real files.
        var filePath = Path.Combine("memory", fileId);

        _files[fileId] = new FileEntry(filePath, originalFileName, interactionId, inputName);
        return (fileId, filePath);
    }

    public void CompleteUpload(int interactionId, string fileId)
    {
        if (_files.TryGetValue(fileId, out var entry) && entry.InteractionId == interactionId)
        {
            bool removeEntry;
            lock (entry)
            {
                removeEntry = entry.State == FileEntryState.DiscardWhenComplete;
                if (entry.State == FileEntryState.Uploading)
                {
                    entry.State = FileEntryState.Uploaded;
                }
            }

            if (removeEntry)
            {
                _files.TryRemove(fileId, out _);
            }
        }
    }

    public IReadOnlyList<InteractionFileUpload> GetFiles(int interactionId, string inputName)
    {
        var files = new List<InteractionFileUpload>();
        foreach (var (fileId, entry) in _files)
        {
            lock (entry)
            {
                if (entry.State is FileEntryState.Uploaded or FileEntryState.Resolved &&
                    entry.InteractionId == interactionId &&
                    string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName))
                {
                    entry.State = FileEntryState.Resolved;
                    files.Add(new InteractionFileUpload(fileId, entry.OriginalFileName, entry.FilePath));
                }
            }
        }

        return files;
    }

    public void RemoveEntry(int interactionId, string fileId)
    {
        if (_files.TryGetValue(fileId, out var entry) && entry.InteractionId == interactionId)
        {
            _files.TryRemove(fileId, out _);
        }
    }

    public void CompleteInteraction(int interactionId)
    {
        CompleteInteractionCallback?.Invoke(interactionId);
        CompletedInteractions.Enqueue(interactionId);

        foreach (var (fileId, entry) in _files)
        {
            bool removeEntry;
            lock (entry)
            {
                removeEntry = entry.InteractionId == interactionId && entry.State == FileEntryState.Uploaded;
                if (entry.InteractionId == interactionId)
                {
                    entry.State = entry.State switch
                    {
                        FileEntryState.Uploading => FileEntryState.DiscardWhenComplete,
                        _ => entry.State
                    };
                }
            }

            if (removeEntry)
            {
                _files.TryRemove(fileId, out _);
            }
        }
    }

    public void CancelInteraction(int interactionId)
    {
        CancelInteractionCallback?.Invoke(interactionId);
        CanceledInteractions.Enqueue(interactionId);

        foreach (var (fileId, entry) in _files)
        {
            bool removeEntry;
            lock (entry)
            {
                removeEntry = entry.InteractionId == interactionId &&
                    entry.State is FileEntryState.Uploaded or FileEntryState.Resolved;
                if (entry.InteractionId == interactionId)
                {
                    entry.State = FileEntryState.DiscardWhenComplete;
                }
            }

            if (removeEntry)
            {
                _files.TryRemove(fileId, out _);
            }
        }
    }

    private sealed class FileEntry(string filePath, string originalFileName, int interactionId, string inputName)
    {
        public string FilePath { get; } = filePath;
        public string OriginalFileName { get; } = originalFileName;
        public int InteractionId { get; } = interactionId;
        public string InputName { get; } = inputName;
        public FileEntryState State { get; set; }
    }

    private enum FileEntryState
    {
        Uploading,
        Uploaded,
        Resolved,
        DiscardWhenComplete
    }
}

internal static class InteractionFileUploadStoreTestExtensions
{
    public static string? GetFilePath(this IInteractionFileUploadStore store, string fileId, int interactionId, string inputName)
    {
        return store.GetFiles(interactionId, inputName).SingleOrDefault(file => file.Id == fileId)?.FilePath;
    }

}
