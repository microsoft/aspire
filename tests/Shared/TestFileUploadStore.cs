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

    public (string FileId, string FilePath) CreateEntry(string originalFileName)
    {
        var fileId = Guid.NewGuid().ToString("N");
        // Use a synthetic path that won't conflict with real files.
        var filePath = Path.Combine("memory", fileId);

        _files[fileId] = new FileEntry(filePath, originalFileName);
        return (fileId, filePath);
    }

    public string? GetFilePath(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? entry.FilePath : null;
    }

    public string? GetFileName(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? entry.OriginalFileName : null;
    }

    public void RemoveEntry(string fileId)
    {
        _files.TryRemove(fileId, out _);
    }

    private sealed record FileEntry(string FilePath, string OriginalFileName);
}
