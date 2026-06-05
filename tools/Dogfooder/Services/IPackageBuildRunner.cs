// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Drives the repository's <c>./build.sh</c> / <c>./build.cmd</c> script to
/// produce locally-built NuGet packages with a controllable version suffix.
/// </summary>
/// <remarks>
/// The package build is decoupled from the embedded shell launch so the user
/// can rebuild between sessions without restarting the TUI, and so the build
/// output can stream into the preparation window before the terminal opens.
/// </remarks>
internal interface IPackageBuildRunner
{
    /// <summary>
    /// Runs the build, streaming each stdout/stderr line through
    /// <paramref name="onLine"/> as it is produced.
    /// </summary>
    Task<PackageBuildResult> RunAsync(
        PackageBuildRequest request,
        Action<string> onLine,
        CancellationToken cancellationToken);
}

/// <param name="VersionSuffix">
/// Value passed to <c>/p:VersionSuffix=</c>. Required: callers should
/// generate a timestamped suffix when the user didn't supply one so each
/// build produces uniquely-versioned <c>.nupkg</c> files.
/// </param>
/// <param name="IncludeNativeBuild">
/// When false the build runs with <c>/p:SkipNativeBuild=true</c>. False is
/// the right default for dogfooding NuGet packages (the AOT step is only
/// needed when the dogfooder is the CLI binary itself).
/// </param>
/// <param name="VersionPrefix">
/// Optional override forwarded to <c>/p:VersionPrefix=</c>. When set, the
/// build stamps every produced <c>.nupkg</c> with this version instead of
/// whatever the repo's <c>eng/Versions.props</c> resolves to. Used by the
/// "Reproduce vCurrent" scenarios so a local build of the in-development
/// branch can produce packages stamped with the actual shipped version.
/// </param>
/// <param name="OutputPackagesDir">
/// Optional destination directory the built <c>.nupkg</c> files should be
/// copied into after the build completes. Arcade's pack target always
/// emits into <c>artifacts/packages/{Configuration}/Shipping</c>; we copy
/// from there into the per-session workspace so each dogfood run owns its
/// own package overlay and the proxy serves session-local bytes rather
/// than whatever was left over from a previous run.
/// </param>
/// <param name="BuildLogPath">
/// Optional explicit log file path; when null, the runner picks a path
/// under <c>artifacts/log/dogfooder/</c>. Used by the session preparer to
/// route the build log into the per-session <c>logs/</c> directory.
/// </param>
internal sealed record PackageBuildRequest(
    string VersionSuffix,
    bool IncludeNativeBuild,
    string? VersionPrefix = null,
    string? OutputPackagesDir = null,
    string? BuildLogPath = null);

/// <param name="Success">Process exit code was zero.</param>
/// <param name="PackagesDirectory">
/// Directory the producer expects packages to land in
/// (<c>artifacts/packages/Debug/Shipping/</c>). Even on failure this is
/// populated so the caller can show "no packages produced" rather than
/// branching on null.
/// </param>
/// <param name="ProducedNupkgPaths">
/// Files matching <c>*.nupkg</c> directly under <see cref="PackagesDirectory"/>
/// at the moment the build finished. Caller passes this to
/// <c>DogfoodingNuGetServer.AddLocalOverrides</c>.
/// </param>
/// <param name="ExitCode">Exit code reported by the build process.</param>
/// <param name="Elapsed">Wall-clock duration of the build.</param>
internal sealed record PackageBuildResult(
    bool Success,
    string PackagesDirectory,
    IReadOnlyList<string> ProducedNupkgPaths,
    int ExitCode,
    TimeSpan Elapsed);
