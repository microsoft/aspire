// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Agents;

/// <summary>
/// Atomically publishes agent configuration JSON after revalidating its tracked inputs.
/// </summary>
internal static class AgentFileWriter
{
    public static async Task<byte[]?> ReadExistingAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing files can be initialized; unreadable files must still fail the update.
            return null;
        }
    }

    public static async Task WriteAsync(
        AgentConfigurationJson.ReadContext.Document document,
        JsonObject root,
        AgentConfigurationJson.ReadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physicalPath = document.Path;
        var destinationExists = document.Bytes is not null;
        var directory = Path.GetDirectoryName(physicalPath)!;
        Directory.CreateDirectory(directory);

        // A sibling keeps replacement on the same filesystem. CreateNew prevents following
        // an existing link or truncating another writer's file; only this invocation's
        // successfully opened staging file is eligible for cleanup.
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(physicalPath)}.aspire-{Guid.NewGuid():N}.tmp");
        var ownsStagingFile = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = destinationExists ? File.GetUnixFileMode(physicalPath) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(stagingPath, options))
            {
                ownsStagingFile = true;
                await JsonSerializer.SerializeAsync(stream, root, JsonSourceGenerationContext.Default.JsonObject, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (!OperatingSystem.IsWindows() && destinationExists)
            {
                // UnixCreateMode is filtered through umask. Restore the destination's exact
                // mode before publication, including group write and executable permissions.
                File.SetUnixFileMode(stagingPath, File.GetUnixFileMode(physicalPath));
            }

            // Recheck byte snapshots, logical links and policy inputs after staging.
            await context.ValidateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (destinationExists)
            {
                // File.Replace retains the destination DACL on Windows. Unix replacement
                // swaps the inode: only the mode bits restored above are preserved, not
                // ownership, custom ACLs or extended attributes.
                File.Replace(stagingPath, physicalPath, destinationBackupFileName: null);
            }
            else
            {
                // Do not overwrite a file created after the caller's optimistic check.
                File.Move(stagingPath, physicalPath);
            }

            ownsStagingFile = false;
        }
        finally
        {
            if (ownsStagingFile)
            {
                File.Delete(stagingPath);
            }
        }
    }
}
