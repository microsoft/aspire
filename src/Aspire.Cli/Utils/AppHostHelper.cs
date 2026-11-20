// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using NuGet.Versioning;
using Semver;

namespace Aspire.Cli.Utils;

internal static class AppHostHelper
{
    /// <summary>
    /// The oldest Aspire.Hosting version whose AppHosts provide the CLI backchannel.
    /// </summary>
    internal static SemVersion MinimumSupportedAspireHostingVersion { get; } = SemVersion.Parse("9.2.0");

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

        if (!TryParseAspireHostingVersion(aspireHostingVersion, out var aspireVersion))
        {
            interactionService.DisplayError(ErrorStrings.CouldNotParseAspireSDKVersion);
            return (false, null);
        }

        if (aspireVersion.ComparePrecedenceTo(MinimumSupportedAspireHostingVersion) < 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.AspireSDKVersionNotSupported, aspireHostingVersion));
            return (false, aspireHostingVersion);
        }

        // NOTE: When we go to support < 9.2.0 app hosts this is where we'll make
        //       a determination as to whether the apphost supports backchannel or not.
        return (true, aspireHostingVersion);
    }

    internal static bool TryParseAspireHostingVersion(string? aspireHostingVersion, out SemVersion version)
    {
        if (SemVersion.TryParse(aspireHostingVersion, SemVersionStyles.Any, out var exactVersion))
        {
            version = exactVersion;
            return true;
        }

        // NuGet floating versions are version ranges rather than concrete semantic versions.
        // Use the range's minimum to conservatively evaluate AppHost capability gates when the
        // resolved package version is unavailable during project evaluation.
        if (!string.IsNullOrWhiteSpace(aspireHostingVersion) &&
            VersionRange.TryParse(aspireHostingVersion, out var range) &&
            range.Float is { } floatingRange &&
            SemVersion.TryParse(floatingRange.MinVersion.ToNormalizedString(), SemVersionStyles.Strict, out var minimumVersion))
        {
            version = minimumVersion;
            return true;
        }

        version = default!;
        return false;
    }

    /// <summary>
    /// Determines whether a declared Aspire.Hosting version is a NuGet floating range, such as
    /// <c>13.*</c> or <c>13.*-*</c>, rather than the concrete version the AppHost runs with.
    /// </summary>
    internal static bool IsFloatingVersion(string? aspireHostingVersion)
        => !string.IsNullOrWhiteSpace(aspireHostingVersion) &&
            !SemVersion.TryParse(aspireHostingVersion, SemVersionStyles.Any, out _) &&
            VersionRange.TryParse(aspireHostingVersion, out var range) &&
            range.Float is not null;

    /// <summary>
    /// Determines whether restore could select a version at or above <paramref name="minimumVersion"/>
    /// for a floating range whose lower bound is below it, such as <c>9.*</c> for a 9.2 minimum.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for concrete versions and for floating ranges that are
    /// wholly on one side of the minimum, because evaluating their lower bound already gives the
    /// correct answer. For example, every version <c>9.1.*</c> can select is below 9.2, and every
    /// version <c>13.*</c> can select is at or above it. Only ranges that cross the minimum need
    /// the concrete version restore selects.
    /// </remarks>
    internal static bool IsFloatingVersionSpanningMinimum(string? aspireHostingVersion, SemVersion minimumVersion)
    {
        if (aspireHostingVersion is null ||
            !IsFloatingVersion(aspireHostingVersion) ||
            !VersionRange.TryParse(aspireHostingVersion, out var range) ||
            range.Float is not { } floatRange ||
            !NuGetVersion.TryParse(minimumVersion.ToString(), out var minimum))
        {
            return false;
        }

        var lowerBound = floatRange.MinVersion;
        if (lowerBound >= minimum)
        {
            return false;
        }

        // The floating segment bounds what restore can select, for example:
        //   9.1.0-*  -> 9.1.0 prereleases or 9.1.0       (below 9.1.1)
        //   9.1.0.*  -> 9.1.0.x                          (below 9.1.1)
        //   9.1.*    -> 9.1.x, optionally prerelease     (below 9.2.0)
        //   9.*      -> 9.x.y, optionally prerelease     (below 10.0.0)
        //   *, *-*   -> any version                      (unbounded)
        // See https://learn.microsoft.com/nuget/concepts/package-versioning#floating-version-resolutions
        NuGetVersion? exclusiveUpperBound = floatRange.FloatBehavior switch
        {
            NuGetVersionFloatBehavior.Prerelease or
            NuGetVersionFloatBehavior.Revision or
            NuGetVersionFloatBehavior.PrereleaseRevision => new NuGetVersion(lowerBound.Major, lowerBound.Minor, lowerBound.Patch + 1),
            NuGetVersionFloatBehavior.Patch or
            NuGetVersionFloatBehavior.PrereleasePatch => new NuGetVersion(lowerBound.Major, lowerBound.Minor + 1, 0),
            NuGetVersionFloatBehavior.Minor or
            NuGetVersionFloatBehavior.PrereleaseMinor => new NuGetVersion(lowerBound.Major + 1, 0, 0),
            _ => null,
        };

        return exclusiveUpperBound is null || minimum < exclusiveUpperBound;
    }

    /// <summary>
    /// Reads the Aspire.Hosting version compiled into an AppHost's build output.
    /// </summary>
    /// <param name="targetPath">The AppHost <c>TargetPath</c>, for example <c>bin/Debug/net10.0/AppHost.dll</c>.</param>
    /// <returns>The concrete package version, or <see langword="null"/> when it cannot be determined.</returns>
    internal static string? TryGetBuiltAspireHostingVersion(string? targetPath)
    {
        // The build copies the restored Aspire.Hosting assembly next to the AppHost for the target
        // framework that actually runs. Its AssemblyInformationalVersion is the package version
        // followed by optional source revision metadata, for example:
        //   13.6.0-preview.1.25520.1+3f1c2a0d...
        // Unlike project.assets.json, this cannot describe stale restore inputs or another target
        // framework, so it is the version that `--no-build` launches and capability checks must use.
        if (string.IsNullOrWhiteSpace(targetPath) || Path.GetDirectoryName(targetPath) is not { Length: > 0 } outputDirectory)
        {
            return null;
        }

        var hostingAssemblyPath = Path.Combine(outputDirectory, "Aspire.Hosting.dll");
        if (!File.Exists(hostingAssemblyPath))
        {
            return null;
        }

        try
        {
            var productVersion = FileVersionInfo.GetVersionInfo(hostingAssemblyPath).ProductVersion;
            return SemVersion.TryParse(productVersion, SemVersionStyles.Any, out var version)
                ? version.WithoutMetadata().ToString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            // Treat an unreadable assembly like missing output; callers fall back to the declared version.
            return null;
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
                new ProcessInvocationOptions(),
                cancellationToken),
            emoji: KnownEmojis.Microscope);

        return appHostInformationResult;
    }

    internal static async Task<int> BuildAppHostAsync(IDotNetCliRunner runner, IInteractionService interactionService, FileInfo projectFile, bool noRestore, IDictionary<string, string>? env, ProcessInvocationOptions options, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(workingDirectory.FullName, projectFile.FullName);
        return await interactionService.ShowStatusAsync(
            $"{InteractionServiceStrings.BuildingAppHost} {relativePath}",
            () => runner.BuildAsync(
                projectFile,
                noRestore,
                env,
                options,
                cancellationToken),
            emoji: KnownEmojis.HammerAndWrench);
    }

}
