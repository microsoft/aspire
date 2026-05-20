// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Finds raw Aspire CLI install candidates for <see cref="InstallationDiscovery"/>.
/// </summary>
internal interface IInstallationCandidateSource
{
    IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context);
}

internal sealed record InstallationCandidateContext(
    string AspireBinaryName,
    DirectoryInfo HomeDirectory,
    DirectoryInfo AspireHomeDirectory,
    IReadOnlyList<InstallationPathHit> PathHits,
    ILogger Logger,
    CancellationToken CancellationToken)
{
    public string AspireHome => AspireHomeDirectory.FullName;
}

internal sealed record InstallationPathHit(string OriginalPath, string CanonicalPath);

internal sealed record InstallationDiscoveryCandidate(string BinaryPath, string Origin);

internal static class InstallationDiscoveryLayout
{
    public const string DogfoodDirectoryName = "dogfood";
}

internal sealed class PathInstallationCandidateSource : IInstallationCandidateSource
{
    public IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context)
    {
        foreach (var pathHit in context.PathHits)
        {
            yield return new InstallationDiscoveryCandidate(pathHit.OriginalPath, "$PATH");
        }
    }
}

internal sealed class ReleasePrefixInstallationCandidateSource : IInstallationCandidateSource
{
    public IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context)
    {
        var releaseDir = Path.Combine(context.AspireHome, "bin");
        var releaseBinary = Path.Combine(releaseDir, context.AspireBinaryName);
        if (File.Exists(releaseBinary))
        {
            context.Logger.LogDebug("Discovery: release prefix walk yielded '{Binary}'.", releaseBinary);
            yield return new InstallationDiscoveryCandidate(releaseBinary, "well-known release prefix");
        }
        else if (Directory.Exists(releaseDir))
        {
            // Bin dir exists but no `aspire` inside it: likely a partially removed install
            // or a third-party `~/.aspire/bin` use. Log so users can correlate with expectations.
            context.Logger.LogDebug(
                "Discovery: release prefix directory '{ReleaseDir}' exists but does not contain an 'aspire' binary — not classifying as a real install.",
                releaseDir);
        }
        else
        {
            context.Logger.LogDebug("Discovery: release prefix '{ReleaseDir}' does not exist; skipping.", releaseDir);
        }
    }
}

internal sealed class DogfoodInstallationCandidateSource : IInstallationCandidateSource
{
    public IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context)
    {
        var dogfoodRoot = Path.Combine(context.AspireHome, InstallationDiscoveryLayout.DogfoodDirectoryName);
        if (Directory.Exists(dogfoodRoot))
        {
            var subdirCount = 0;
            foreach (var prDir in InstallationCandidateSourceHelpers.EnumerateDirectoriesSafe(dogfoodRoot, context.Logger))
            {
                // Match the per-step cancellation cadence of the dotnet-tool-store source so
                // Ctrl+C on a slow dogfood root doesn't have to wait for the whole walk.
                context.CancellationToken.ThrowIfCancellationRequested();
                subdirCount++;
                var binDir = Path.Combine(prDir, "bin");
                var binary = Path.Combine(binDir, context.AspireBinaryName);
                if (File.Exists(binary))
                {
                    context.Logger.LogDebug("Discovery: dogfood walk yielded '{Binary}'.", binary);
                    yield return new InstallationDiscoveryCandidate(binary, "dogfood prefix");
                }
                else
                {
                    // A dogfood pr-N directory without bin/aspire is most commonly a stale
                    // leftover from a failed install or partial uninstall.
                    context.Logger.LogDebug(
                        "Discovery: dogfood directory '{PrDir}' exists but does not contain a '{Bin}/aspire' binary — not classifying as a real install.",
                        prDir, "bin");
                }
            }

            if (subdirCount == 0)
            {
                context.Logger.LogDebug("Discovery: dogfood root '{DogfoodRoot}' exists but contains no subdirectories.", dogfoodRoot);
            }
        }
        else
        {
            context.Logger.LogDebug("Discovery: dogfood root '{DogfoodRoot}' does not exist; skipping.", dogfoodRoot);
        }
    }
}

internal sealed class DotnetToolStoreInstallationCandidateSource : IInstallationCandidateSource
{
    // Streaming-enumeration options used for the dotnet-tool store walk. The tool store
    // is recursive (~/.dotnet/tools/.store/aspire.cli/<version>/aspire.cli.<rid>/<version>/tools/...)
    // so we recurse, but we skip inaccessible subtrees instead of throwing mid-walk.
    // That lets the surrounding foreach observe each yielded entry — and the caller's
    // cancellation token — incrementally, instead of having to materialize the whole
    // tree before the discovery loop can react to Ctrl+C on a slow filesystem.
    //
    // Reparse points (symlinks / junctions) are skipped so a symlink cycle anywhere
    // under the store cannot make `Directory.EnumerateFiles` walk indefinitely. The
    // legitimate tool-store layout contains no symlinks, so this loses nothing real
    // and removes a self-DoS surface from `aspire doctor` discovery.
    private static readonly EnumerationOptions s_enumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context)
    {
        var toolStore = Path.Combine(context.HomeDirectory.FullName, ".dotnet", "tools", ".store", "aspire.cli");
        if (!Directory.Exists(toolStore))
        {
            context.Logger.LogDebug("Discovery: dotnet-tool store '{ToolStore}' does not exist; skipping.", toolStore);
            yield break;
        }

        // Directory.EnumerateFiles is deferred — construction never throws, the first
        // MoveNext does. Iterator methods cannot have yields inside a catch clause, so
        // we drive the enumerator manually: MoveNext lives in try/catch and yields run
        // in the unguarded path. That lets us swallow IOExceptions on the root (e.g.
        // mode 000 on a perm-denied tool store) and on any directory we still hit during
        // recursion that IgnoreInaccessible does not cover (mid-walk filesystem races).
        using var enumerator = Directory.EnumerateFiles(toolStore, context.AspireBinaryName, s_enumerationOptions).GetEnumerator();

        var anyMatch = false;
        while (true)
        {
            // Honor cancellation between steps so Ctrl+C on a slow/large tool store
            // doesn't have to wait for the entire recursive walk to complete.
            context.CancellationToken.ThrowIfCancellationRequested();

            bool moved;
            string? current = null;
            try
            {
                moved = enumerator.MoveNext();
                if (moved)
                {
                    current = enumerator.Current;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                context.Logger.LogDebug(ex, "Discovery: failed to enumerate files under '{Root}'.", toolStore);
                yield break;
            }

            if (!moved)
            {
                break;
            }

            anyMatch = true;
            context.Logger.LogDebug("Discovery: dotnet-tool store walk yielded '{Binary}'.", current);
            yield return new InstallationDiscoveryCandidate(current!, "dotnet-tool store");
        }

        if (!anyMatch)
        {
            context.Logger.LogDebug(
                "Discovery: dotnet-tool store '{ToolStore}' exists but contains no '{BinaryName}' binary — not classifying as a real install.",
                toolStore, context.AspireBinaryName);
        }
    }
}

internal static class InstallationCandidateSourceHelpers
{
    // Streams subdirectories of <paramref name="root"/> so the caller can observe each
    // entry — and the surrounding cancellation token — incrementally. Materializing the
    // whole list upfront would defeat the per-step cancellation cadence the dogfood
    // walker relies on (parallel to the dotnet-tool-store source which streams via a
    // manually-driven enumerator). Iterator methods cannot yield inside a catch, so we
    // drive the enumerator manually and swallow IOExceptions raised on initial open or
    // mid-walk.
    public static IEnumerable<string> EnumerateDirectoriesSafe(string root, ILogger logger)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateDirectories(root).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Discovery: failed to enumerate directories under '{Root}'.", root);
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    logger.LogDebug(ex, "Discovery: failed to enumerate directories under '{Root}'.", root);
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }
}
