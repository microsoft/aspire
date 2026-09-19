// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Stages and atomically publishes one file after the caller validates its original inputs.
/// </summary>
internal static class AgentFileCommitter
{
    public static async Task CommitAsync(
        string physicalPath,
        bool destinationExists,
        Func<Stream, CancellationToken, Task> writeContent,
        Func<CancellationToken, Task> validateBeforeCommit,
        UnixFileMode? newFileMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                options.UnixCreateMode = destinationExists ? File.GetUnixFileMode(physicalPath) : newFileMode;
            }

            await using (var stream = new FileStream(stagingPath, options))
            {
                ownsStagingFile = true;
                await writeContent(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (!OperatingSystem.IsWindows() && destinationExists)
            {
                // UnixCreateMode is filtered through umask. Restore the destination's exact
                // mode before publication, including group write and executable permissions.
                File.SetUnixFileMode(stagingPath, File.GetUnixFileMode(physicalPath));
            }

            // The caller owns byte snapshots, logical-link evidence and any additional
            // read-only inputs (for example native policy files). Recheck after staging.
            await validateBeforeCommit(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (destinationExists)
            {
                // Replace preserves destination metadata/ACLs, unlike delete-then-move.
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
