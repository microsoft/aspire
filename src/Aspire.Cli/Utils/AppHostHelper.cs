// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
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

}
