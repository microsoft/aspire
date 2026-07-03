// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Hosting.Dashboard;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Utils;

/// <summary>
/// An in-memory implementation of <see cref="IFileUploadStore"/> for tests.
/// Does not write to disk or implement IDisposable.
/// </summary>
internal sealed class InMemoryFileUploadStore : IFileUploadStore
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

    public IReadOnlyList<InputFileDto>? ResolveFileReferences(string? jsonValue, string inputName, ILogger logger)
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
        catch (JsonException)
        {
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
            var filePath = GetFilePath(fileRef.Id);
            if (filePath is null)
            {
                continue;
            }
            var fileName = string.IsNullOrEmpty(fileRef.Name) ? GetFileName(fileRef.Id) ?? "" : fileRef.Name;
            files.Add(new InputFileDto(fileRef.Id, fileName, filePath));
        }

        return files.Count > 0 ? files : null;
    }

    private sealed record FileEntry(string FilePath, string OriginalFileName);

    private sealed class FileReference
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
