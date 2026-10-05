// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Stores uploaded files and maps file IDs to their paths or content.
/// </summary>
internal interface IInteractionFileUploadStore
{
    /// <summary>
    /// Registers an interaction and the file inputs that can own uploaded files.
    /// </summary>
    void StartInteraction(int interactionId, IReadOnlyList<(string InputName, int MaxFileCount)> fileInputs);

    /// <summary>
    /// Creates a new entry for an uploaded file and returns the file ID and path.
    /// </summary>
    (string FileId, string FilePath) CreateEntry(string originalFileName, int interactionId, string inputName);

    /// <summary>
    /// Marks a file upload for an interaction as successfully completed.
    /// </summary>
    void CompleteUpload(int interactionId, string fileId);

    /// <summary>
    /// Gets the completed uploads for an interaction input.
    /// </summary>
    IReadOnlyList<InteractionFileUpload> GetCompletedFiles(int interactionId, string inputName);

    /// <summary>
    /// Marks validated uploads as resolved into the accepted interaction result.
    /// </summary>
    void MarkFilesAccepted(int interactionId, string inputName, IReadOnlyList<string> fileIds);

    /// <summary>
    /// Removes a file entry from an interaction and cleans up its uploaded content.
    /// </summary>
    void RemoveEntry(int interactionId, string fileId);

    /// <summary>
    /// Marks an interaction as completed while retaining uploads for the caller to process.
    /// </summary>
    void CompleteInteraction(int interactionId);

    /// <summary>
    /// Cancels an interaction and removes its completed uploads.
    /// </summary>
    void CancelInteraction(int interactionId);
}

internal sealed record InteractionFileUpload(string Id, string Name, string FilePath);
