// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.Utils;

/// <summary>
/// An in-memory implementation of <see cref="IFileUploadStore"/> for tests.
/// Does not write to disk or implement IDisposable.
/// </summary>
internal sealed class TestFileUploadStore : IFileUploadStore
{
    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.Ordinal);

    public ConcurrentQueue<int> StartedInteractions { get; } = new();
    public ConcurrentQueue<(int InteractionId, IReadOnlyList<InteractionFile> Files)> CompletedInteractions { get; } = new();
    public ConcurrentQueue<int> CanceledInteractions { get; } = new();

    public void StartInteraction(int interactionId)
    {
        StartedInteractions.Enqueue(interactionId);
    }

    public (string FileId, string FilePath) CreateEntry(string originalFileName, int interactionId, string inputName)
    {
        var fileId = Guid.NewGuid().ToString("N");
        // Use a synthetic path that won't conflict with real files.
        var filePath = Path.Combine("memory", fileId);

        _files[fileId] = new FileEntry(filePath, originalFileName, interactionId, inputName);
        return (fileId, filePath);
    }

    public void CompleteUpload(string fileId)
    {
    }

    public string? GetFilePath(string fileId, string inputName)
    {
        return _files.TryGetValue(fileId, out var entry) &&
            string.Equals(entry.InputName, inputName, StringComparisons.InteractionInputName)
                ? entry.FilePath
                : null;
    }

    public string? GetFileName(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? entry.OriginalFileName : null;
    }

    public void RemoveEntry(string fileId)
    {
        _files.TryRemove(fileId, out _);
    }

    public void CompleteInteraction(int interactionId, IReadOnlyList<InteractionFile> files)
    {
        CompletedInteractions.Enqueue((interactionId, files));
    }

    public void CancelInteraction(int interactionId)
    {
        CanceledInteractions.Enqueue(interactionId);

        foreach (var (fileId, entry) in _files)
        {
            if (entry.InteractionId == interactionId)
            {
                _files.TryRemove(fileId, out _);
            }
        }
    }

    private sealed record FileEntry(string FilePath, string OriginalFileName, int InteractionId, string InputName);
}
