// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;

namespace Aspire.Cli.Utils;

internal static class CliPathHelper
{
    // The maximum age before a leftover cli.sock.* file in the runtime sockets directory is
    // pruned. 24 hours is comfortably longer than any legitimate Aspire CLI run and short enough
    // that stale entries don't pile up indefinitely after crashes (see issue #16709).
    internal static readonly TimeSpan s_staleSocketThreshold = TimeSpan.FromHours(24);

    private static int s_socketDirectorySwept;

    internal static string GetAspireHomeDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");

    /// <summary>
    /// Creates a randomized CLI-managed socket path.
    /// </summary>
    /// <param name="socketPrefix">The socket file prefix.</param>
    internal static string CreateUnixDomainSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix, isGuestAppHost: false);

    internal static string CreateGuestAppHostSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix, isGuestAppHost: true);

    /// <summary>
    /// Prunes leftover CLI socket files from <c>~/.aspire/cli/runtime/sockets/</c> whose last
    /// modified timestamp is older than <paramref name="maxAge"/>. Returns the number of files
    /// that were deleted. Exceptions from individual file deletions are swallowed so a single
    /// permission-denied or locked file can't break startup. Exposed for tests via
    /// <see cref="CleanupStaleCliSockets(string, TimeSpan, TimeProvider)"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="BackchannelConstants.CleanupOrphanedSockets(string, string, int)"/>,
    /// CLI sockets don't encode the process ID in their filename — they're created with a random
    /// GUID-style suffix — so the only reliable signal we have for "this is stale" is the file's
    /// mtime. We pick a generous default threshold so an in-flight long-running run never has its
    /// socket pruned out from under it.
    /// </remarks>
    internal static int CleanupStaleCliSockets(string socketDirectory, TimeSpan maxAge, TimeProvider? timeProvider = null)
    {
        if (!Directory.Exists(socketDirectory))
        {
            return 0;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(socketDirectory, "cli.sock.*"))
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (now - lastWrite >= maxAge)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch
            {
                // Best-effort cleanup; one bad file should not block CLI startup.
            }
        }

        return deleted;
    }

    private static string CreateSocketPath(string socketPrefix, bool isGuestAppHost)
    {
        var socketName = $"{socketPrefix}.{BackchannelConstants.CreateRandomIdentifier()}";

        if (isGuestAppHost && OperatingSystem.IsWindows())
        {
            return socketName;
        }

        var socketDirectory = GetCliSocketDirectory();
        Directory.CreateDirectory(socketDirectory);

        if (Interlocked.CompareExchange(ref s_socketDirectorySwept, 1, 0) == 0)
        {
            CleanupStaleCliSockets(socketDirectory, s_staleSocketThreshold);
        }

        return Path.Combine(socketDirectory, socketName);
    }

    private static string GetCliHomeDirectory()
        => Path.Combine(GetAspireHomeDirectory(), "cli");

    private static string GetCliRuntimeDirectory()
        => Path.Combine(GetCliHomeDirectory(), "runtime");

    private static string GetCliSocketDirectory()
        => Path.Combine(GetCliRuntimeDirectory(), "sockets");
}
