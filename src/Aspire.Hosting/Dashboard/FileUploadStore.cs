// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Stores uploaded files from the Dashboard and maps file IDs to their temporary paths on disk.
/// </summary>
internal sealed class FileUploadStore : IFileUploadStore, IDisposable
{
    private readonly ConcurrentDictionary<string, TempFile> _files = new(StringComparer.Ordinal);
    private readonly ITempFileSystemService _tempFileSystem;

    public FileUploadStore(IFileSystemService fileSystemService)
    {
        _tempFileSystem = fileSystemService.TempDirectory;
    }

    /// <summary>
    /// Creates a new temp file path and returns the file ID and path.
    /// </summary>
    public (string FileId, string FilePath) CreateEntry(string originalFileName)
    {
        // Sanitize the file name to prevent path traversal attacks.
        // Strip directory components for both Unix (/) and Windows (\) separators
        // regardless of the current platform, since the name comes from a remote client.
        var lastSep = originalFileName.AsSpan().LastIndexOfAny('/', '\\');
        var safeName = lastSep >= 0 ? originalFileName[(lastSep + 1)..] : originalFileName;

        var tempFile = _tempFileSystem.CreateTempFile(string.IsNullOrEmpty(safeName) ? null : safeName);
        var fileId = Guid.NewGuid().ToString("N");

        _files[fileId] = tempFile;
        return (fileId, tempFile.Path);
    }

    /// <summary>
    /// Gets the file path for a given file ID.
    /// </summary>
    public string? GetFilePath(string fileId)
    {
        return _files.TryGetValue(fileId, out var tempFile) ? tempFile.Path : null;
    }

    /// <summary>
    /// Gets the original file name for a given file ID.
    /// </summary>
    public string? GetFileName(string fileId)
    {
        return _files.TryGetValue(fileId, out var tempFile) ? Path.GetFileName(tempFile.Path) : null;
    }

    /// <summary>
    /// Removes a file entry and deletes the associated file on disk.
    /// Used to clean up after failed uploads.
    /// </summary>
    public void RemoveEntry(string fileId)
    {
        if (_files.TryRemove(fileId, out var tempFile))
        {
            try
            {
                tempFile.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
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
            var filePath = store.GetFilePath(fileRef.Id);
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
        foreach (var tempFile in _files.Values)
        {
            try
            {
                tempFile.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
        _files.Clear();
    }

    // Shared type used by ResolveFileReferences for JSON deserialization of file input values.
    // The shape matches what the Dashboard sends: [{"Id":"...","Name":"..."}]
    private sealed class FileReference
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
