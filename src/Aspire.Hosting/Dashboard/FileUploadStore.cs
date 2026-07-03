// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Stores uploaded files from the Dashboard and maps file IDs to their temporary paths on disk.
/// </summary>
internal sealed class FileUploadStore : IDisposable
{
    private readonly ConcurrentDictionary<string, FileUploadEntry> _files = new(StringComparer.Ordinal);
    private readonly string _uploadDirectory;

    public FileUploadStore()
    {
        // Use a process-specific subdirectory to prevent symlink attacks on multi-user systems.
        _uploadDirectory = Path.Combine(Path.GetTempPath(), $"aspire-uploads-{Environment.ProcessId}");
        Directory.CreateDirectory(_uploadDirectory);
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
}
