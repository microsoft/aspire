// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Stores uploaded files and maps file IDs to their paths or content.
/// </summary>
internal interface IInteractionFileUploadStore
{
    /// <summary>
    /// Registers an interaction that can own uploaded files.
    /// </summary>
    void StartInteraction(int interactionId);

    /// <summary>
    /// Creates a new entry for an uploaded file and returns the file ID and path.
    /// </summary>
    (string FileId, string FilePath) CreateEntry(string originalFileName, int interactionId, string inputName);

    /// <summary>
    /// Marks a file upload for an interaction as successfully completed.
    /// </summary>
    void CompleteUpload(int interactionId, string fileId);

    /// <summary>
    /// Gets the file path for a given file ID, interaction ID, and input name.
    /// </summary>
    string? GetFilePath(string fileId, int interactionId, string inputName);

    /// <summary>
    /// Gets the original file name for a given interaction and file ID.
    /// </summary>
    string? GetFileName(int interactionId, string fileId);

    /// <summary>
    /// Removes a file entry from an interaction and cleans up its uploaded content.
    /// </summary>
    void RemoveEntry(int interactionId, string fileId);

    /// <summary>
    /// Marks an interaction as completed.
    /// </summary>
    void CompleteInteraction(int interactionId);

    /// <summary>
    /// Cancels an interaction and removes its completed uploads.
    /// </summary>
    void CancelInteraction(int interactionId);
}
