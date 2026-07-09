// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Utils;

internal static class AppHostHelper
{
    internal static async Task<(bool IsCompatibleAppHost, string? AspireHostingVersion)> CheckAppHostCompatibilityAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, AspireCliTelemetry telemetry, DirectoryInfo workingDirectory, string logFilePath, CancellationToken cancellationToken)
    {
        var appHostInformation = await GetAppHostInformationAsync(runner, interactionService, projectFile, telemetry, workingDirectory, cancellationToken);

        return EvaluateAppHostCompatibility(
            appHostInformation.ExitCode,
            appHostInformation.IsAspireHost,
            appHostInformation.AspireHostingVersion,
            interactionService,
            logFilePath);
    }

    /// <summary>
    /// Applies the SemVer minimum-version gate (and user-facing error display) for an AppHost
    /// using already-fetched project information. Use this when the caller has cached the
    /// MSBuild result and wants to avoid issuing another <c>dotnet msbuild -getProperty</c>
    /// invocation to evaluate compatibility.
    /// </summary>
    internal static (bool IsCompatibleAppHost, string? AspireHostingVersion) EvaluateAppHostCompatibility(
        int exitCode,
        bool isAspireHost,
        string? aspireHostingVersion,
        IInteractionService interactionService,
        string logFilePath)
    {
        if (exitCode != 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectCouldNotBeAnalyzed, logFilePath));
            return (false, null);
        }

        if (!isAspireHost)
        {
            interactionService.DisplayError(ErrorStrings.ProjectIsNotAppHost);
            return (false, null);
        }

        if (!SemVersion.TryParse(aspireHostingVersion, out var aspireVersion))
        {
            interactionService.DisplayError(ErrorStrings.CouldNotParseAspireSDKVersion);
            return (false, null);
        }

        var minimumVersion = SemVersion.Parse("9.2.0");
        if (aspireVersion.ComparePrecedenceTo(minimumVersion) < 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.AspireSDKVersionNotSupported, aspireHostingVersion));
            return (false, aspireHostingVersion);
        }

        // NOTE: When we go to support < 9.2.0 app hosts this is where we'll make
        //       a determination as to whether the apphost supports backchannel or not.
        return (true, aspireHostingVersion);
    }

    internal static async Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, AspireCliTelemetry telemetry, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var relativePath = Path.GetRelativePath(workingDirectory.FullName, projectFile.FullName);
        var appHostInformationResult = await interactionService.ShowStatusAsync(
            $"{InteractionServiceStrings.CheckingProjectType}: {relativePath}",
            () => runner.GetAppHostInformationAsync(
                projectFile,
                new ProcessInvocationOptions(),
                cancellationToken),
            emoji: KnownEmojis.Microscope);

        return appHostInformationResult;
    }

    internal static async Task<int> BuildAppHostAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, bool noRestore, ProcessInvocationOptions options, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(workingDirectory.FullName, projectFile.FullName);
        return await interactionService.ShowStatusAsync(
            $"{InteractionServiceStrings.BuildingAppHost} {relativePath}",
            () => runner.BuildAsync(
                projectFile,
                noRestore,
                options,
                cancellationToken),
            emoji: KnownEmojis.HammerAndWrench);
    }

    /// <summary>
    /// Computes the auxiliary backchannel socket path prefix for a given AppHost project file.
    /// </summary>
    /// <remarks>
    /// Since socket names now include a randomized instance ID and the AppHost's PID
    /// (e.g., <c>{appHostId}{instanceId}.{pid}</c>),
    /// the CLI cannot compute the exact socket path. Use this prefix with a glob pattern
    /// to find matching sockets, or use <see cref="FindMatchingNonOrphanedSockets"/> instead.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file or assembly.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The computed socket path prefix (without PID suffix).</returns>
    internal static string ComputeAuxiliarySocketPrefix(string appHostPath, string homeDirectory)
        => BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

    /// <summary>
    /// Computes the legacy (pre-normalization) hash for backward compatibility with older AppHosts.
    /// </summary>
    /// <param name="appHostPath">The full path to the AppHost project file or assembly.</param>
    /// <returns>The legacy hash, or <c>null</c> if it is identical to the current hash.</returns>
    internal static string? ComputeLegacyHash(string appHostPath)
        => BackchannelConstants.ComputeLegacyHash(appHostPath);

    /// <summary>
    /// Computes all legacy hashes for backward compatibility with older AppHosts.
    /// </summary>
    /// <param name="appHostPath">The full path to the AppHost project file or assembly.</param>
    /// <returns>The legacy hashes to search.</returns>
    internal static string[] ComputeLegacyHashes(string appHostPath)
        => BackchannelConstants.ComputeLegacyHashes(appHostPath);

    /// <summary>
    /// Finds matching socket files and deletes PID-qualified sockets whose owning process has exited.
    /// </summary>
    internal static string[] FindMatchingNonOrphanedSockets(
        string appHostPath,
        string homeDirectory,
        int currentPid,
        ILogger logger)
    {
        // Resolve symlinks so callers that provide "/tmp/..." can still match sockets keyed
        // off the physical path (for example "/private/tmp/..." on macOS).
        var resolvedPath = PathNormalizer.ResolveSymlinks(appHostPath);
        var matchingSockets = BackchannelConstants.FindMatchingSockets(resolvedPath, homeDirectory);
        var remainingSockets = PruneOrphanedSockets(matchingSockets, currentPid, logger, out var deletedCount);
        if (deletedCount > 0)
        {
            logger.LogDebug("Cleaned up {Count} orphaned AppHost socket(s).", deletedCount);
        }

        return remainingSockets;
    }

    /// <summary>
    /// Best-effort deletion of an auxiliary backchannel socket file whose owning AppHost instance is no longer running.
    /// </summary>
    /// <remarks>
    /// This is the single CLI-side socket-cleanup path. It is used both at stop time (<c>aspire stop</c> and
    /// <see cref="Projects.RunningInstanceManager"/>, once the process is confirmed terminated) and by the orphan-pruning
    /// backstop in <see cref="PruneOrphanedSockets"/> (once the owning PID is observed to be dead). Leaving the socket
    /// behind causes a later command (for example <c>aspire add</c> or <c>aspire stop</c>) to rediscover it via
    /// <see cref="FindMatchingNonOrphanedSockets"/> and attempt to connect to a now-dead process. This is most visible on
    /// Windows, where the dead AppHost's PID can be reused so the orphan-pruning heuristic still believes the process is
    /// alive. Deleting by exact path at stop time sidesteps that PID heuristic entirely. The caller must only invoke this
    /// once it knows the owning process has terminated. The AppHost-side socket cleanup in <c>Aspire.Hosting</c>
    /// (<c>BackchannelService</c>/<c>AuxiliaryBackchannelService</c>) deliberately does not share this method: it lives in a
    /// different assembly and deletes a socket the AppHost itself owns, so it is not exposed to the PID-reuse problem. See
    /// https://github.com/microsoft/aspire/issues/17587.
    /// </remarks>
    /// <param name="socketPath">The path to the auxiliary backchannel socket file.</param>
    /// <param name="logger">Logger used for diagnostic output.</param>
    /// <returns><see langword="true"/> if the socket file was deleted; otherwise <see langword="false"/>.</returns>
    internal static bool TryDeleteSocketFile(string socketPath, ILogger logger)
    {
        try
        {
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
                logger.LogDebug("Cleaned up backchannel socket file: {SocketPath}", socketPath);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed delete is non-fatal: the next command's orphan-pruning pass will attempt cleanup again. We swallow
            // the same exception types as the other socket-cleanup sites for consistency.
            logger.LogDebug(ex, "Failed to clean up backchannel socket file (this may be safe to ignore): {SocketPath}", socketPath);
        }

        return false;
    }

    /// <summary>
    /// Deletes PID-qualified socket files whose owning process has exited and returns sockets that should still be probed.
    /// </summary>
    private static string[] PruneOrphanedSockets(string[] socketPaths, int currentPid, ILogger logger, out int deletedCount)
    {
        deletedCount = 0;
        var remainingSockets = new List<string>(socketPaths.Length);

        foreach (var socketPath in socketPaths)
        {
            var pid = BackchannelConstants.ExtractPid(socketPath);
            if (pid is { } pidValue && pidValue != currentPid && !BackchannelConstants.ProcessExists(pidValue))
            {
                if (!BackchannelConstants.ProcessExists(pidValue))
                {
                    // Socket names include the owning PID in the compact/current legacy formats:
                    //   {appHostId}{instanceId}.{pid}
                    //   auxi.sock.{hash}.{instanceHash}.{pid}
                    // After a crash or reboot these files can remain, and connecting to them
                    // reports "connection refused" even though there is no AppHost to stop.
                    if (TryDeleteSocketFile(socketPath, logger))
                    {
                        deletedCount++;
                    }

                    continue;
                }
            }

            remainingSockets.Add(socketPath);
        }

        return [.. remainingSockets];
    }

    /// <summary>
    /// Extracts the hash portion from an auxiliary socket path.
    /// </summary>
    /// <remarks>
    /// Works with compact format (<c>{appHostId}{instanceId}.{pid}</c>), old format (<c>auxi.sock.{hash}</c>),
    /// previous format (<c>auxi.sock.{hash}.{pid}</c>), and legacy current format
    /// (<c>auxi.sock.{hash}.{instanceHash}.{pid}</c>).
    /// </remarks>
    /// <param name="socketPath">The full socket path (e.g., "/path/to/auxi.sock.b67075ff12d56865.a1b2c3d4e5f6.12345").</param>
    /// <returns>The hash portion (e.g., "b67075ff12d56865"), or null if the format is unrecognized.</returns>
    internal static string? ExtractHashFromSocketPath(string socketPath)
        => BackchannelConstants.ExtractHash(socketPath);

    /// <summary>
    /// Extracts the PID from an auxiliary socket path when one is present.
    /// </summary>
    /// <param name="socketPath">The full socket path.</param>
    /// <returns>The PID if present and valid, or null for old format sockets.</returns>
    internal static int? ExtractPidFromSocketPath(string socketPath)
        => BackchannelConstants.ExtractPid(socketPath);

    /// <summary>
    /// Checks if a process with the given PID exists and is running.
    /// </summary>
    /// <param name="pid">The process ID to check.</param>
    /// <returns>True if the process exists and is running; otherwise, false.</returns>
    internal static bool ProcessExists(int pid)
        => BackchannelConstants.ProcessExists(pid);

    /// <summary>
    /// Cleans up orphaned socket files for a specific AppHost hash.
    /// </summary>
    /// <param name="backchannelsDirectory">The backchannels directory path.</param>
    /// <param name="hash">The AppHost hash to match.</param>
    /// <param name="currentPid">The current process ID (to avoid deleting own socket).</param>
    /// <returns>The number of orphaned sockets deleted.</returns>
    internal static int CleanupOrphanedSockets(string backchannelsDirectory, string hash, int currentPid)
        => BackchannelConstants.CleanupOrphanedSockets(backchannelsDirectory, hash, currentPid);
}
