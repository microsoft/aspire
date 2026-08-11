// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Stores uploaded files and maps file IDs to their paths or content.
/// </summary>
internal interface IFileUploadStore
{
    /// <summary>
    /// Creates a new entry for an uploaded file and returns the file ID and path.
    /// </summary>
    (string FileId, string FilePath) CreateEntry(string originalFileName);

    /// <summary>
    /// Gets the file path for a given file ID.
    /// </summary>
    string? GetFilePath(string fileId);

    /// <summary>
    /// Gets the original file name for a given file ID.
    /// </summary>
    string? GetFileName(string fileId);

    /// <summary>
    /// Removes a file entry. Used to clean up after failed uploads.
    /// </summary>
    void RemoveEntry(string fileId);
}
