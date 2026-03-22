// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Hosting.Backchannel;
using Semver;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils;

internal static class AppHostHelper
{
    internal static async Task<(bool IsCompatibleAppHost, bool SupportsBackchannel, string? AspireHostingVersion)> CheckAppHostCompatibilityAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, AspireCliTelemetry telemetry, DirectoryInfo workingDirectory, string logFilePath, CancellationToken cancellationToken)
    {
        var appHostInformation = await GetAppHostInformationAsync(runner, interactionService, projectFile, telemetry, workingDirectory, cancellationToken);

        if (appHostInformation.ExitCode != 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectCouldNotBeAnalyzed, logFilePath));
            return (false, false, null);
        }

        if (!appHostInformation.IsAspireHost)
        {
            interactionService.DisplayError(ErrorStrings.ProjectIsNotAppHost);
            return (false, false, null);
        }

        if (!SemVersion.TryParse(appHostInformation.AspireHostingVersion, out var aspireVersion))
        {
            interactionService.DisplayError(ErrorStrings.CouldNotParseAspireSDKVersion);
            return (false, false, null);
        }

        var minimumVersion = SemVersion.Parse("9.2.0");
        if (aspireVersion.ComparePrecedenceTo(minimumVersion) < 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.AspireSDKVersionNotSupported, appHostInformation.AspireHostingVersion));
            return (false, false, appHostInformation.AspireHostingVersion);
        }
        else
        {
            // NOTE: When we go to support < 9.2.0 app hosts this is where we'll make
            //       a determination as to whether the apphsot supports backchannel or not.
            return (true, true, appHostInformation.AspireHostingVersion);
        }
    }

    internal static async Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, AspireCliTelemetry telemetry, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var relativePath = Path.GetRelativePath(workingDirectory.FullName, projectFile.FullName);
        var appHostInformationResult = await interactionService.ShowStatusAsync(
            $"{InteractionServiceStrings.CheckingProjectType}: {relativePath}",
            () => runner.GetAppHostInformationAsync(
                projectFile,
                new DotNetCliRunnerInvocationOptions(),
                cancellationToken),
            emoji: KnownEmojis.Microscope);

        return appHostInformationResult;
    }

    internal static async Task<int> BuildAppHostAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, bool noRestore, DotNetCliRunnerInvocationOptions options, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
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
    /// Since socket names now include the AppHost's PID (e.g., <c>auxi.sock.{hash}.{pid}</c>),
    /// the CLI cannot compute the exact socket path. Use this prefix with a glob pattern
    /// to find matching sockets, or use <see cref="FindMatchingSockets"/> instead.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file or assembly.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The computed socket path prefix (without PID suffix).</returns>
    internal static string ComputeAuxiliarySocketPrefix(string appHostPath, string homeDirectory)
        => BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

    /// <summary>
    /// Finds all socket files matching the given AppHost path.
    /// </summary>
    /// <param name="appHostPath">The full path to the AppHost project file or assembly.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>An array of socket file paths, or empty if none found.</returns>
    internal static string[] FindMatchingSockets(string appHostPath, string homeDirectory)
        => BackchannelConstants.FindMatchingSockets(appHostPath, homeDirectory);

    /// <summary>
    /// Extracts the hash portion from an auxiliary socket path.
    /// </summary>
    /// <remarks>
    /// Works with both old format (<c>auxi.sock.{hash}</c>) and new format (<c>auxi.sock.{hash}.{pid}</c>).
    /// </remarks>
    /// <param name="socketPath">The full socket path (e.g., "/path/to/auxi.sock.b67075ff12d56865.12345").</param>
    /// <returns>The hash portion (e.g., "b67075ff12d56865"), or null if the format is unrecognized.</returns>
    internal static string? ExtractHashFromSocketPath(string socketPath)
        => BackchannelConstants.ExtractHash(socketPath);

    /// <summary>
    /// Extracts the PID from an auxiliary socket path (new format only).
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
