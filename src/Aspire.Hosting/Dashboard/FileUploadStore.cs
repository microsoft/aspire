// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    private readonly ConcurrentDictionary<string, FileUploadEntry> _files = new(StringComparer.Ordinal);
    private readonly string _uploadDirectory;

    public FileUploadStore()
    {
        // Use CreateTempSubdirectory to atomically create a randomly named directory with
        // restrictive permissions, avoiding predictable paths that are vulnerable to symlink attacks.
        var tempDir = Directory.CreateTempSubdirectory("aspire-uploads-");
        _uploadDirectory = tempDir.FullName;
    }

    /// <summary>
    /// Creates a new temp file path and returns the file ID and path.
    /// </summary>
    public (string FileId, string FilePath) CreateEntry(string originalFileName)
    {
        var fileId = Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(_uploadDirectory, fileId);

        _files[fileId] = new FileUploadEntry(filePath, originalFileName);
        return (fileId, filePath);
    }

    /// <summary>
    /// Gets the file path for a given file ID.
    /// </summary>
    public string? GetFilePath(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? entry.FilePath : null;
    }

    /// <summary>
    /// Gets the original file name for a given file ID.
    /// </summary>
    public string? GetFileName(string fileId)
    {
        return _files.TryGetValue(fileId, out var entry) ? entry.OriginalFileName : null;
    }

    /// <summary>
    /// Removes a file entry and deletes the associated file on disk.
    /// Used to clean up after failed uploads.
    /// </summary>
    public void RemoveEntry(string fileId)
    {
        if (_files.TryRemove(fileId, out var entry))
        {
            try
            {
                File.Delete(entry.FilePath);
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
        try
        {
            if (Directory.Exists(_uploadDirectory))
            {
                Directory.Delete(_uploadDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed record FileUploadEntry(string FilePath, string OriginalFileName);

    // Shared type used by ResolveFileReferences for JSON deserialization of file input values.
    // The shape matches what the Dashboard sends: [{"Id":"...","Name":"..."}]
    private sealed class FileReference
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
