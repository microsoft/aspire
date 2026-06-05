// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios;

/// <summary>
/// Concrete settings the preparer applies for a scenario invocation. Each
/// scenario's <c>Build</c> method emits one of these; the preparer is then
/// scenario-agnostic — it consumes the plan with the same build → proxy →
/// env-overrides pipeline regardless of which scenario produced it.
/// </summary>
/// <param name="Channel">
/// Channel identity the CLI should report via <c>ASPIRE_CLI_CHANNEL</c>.
/// </param>
/// <param name="PrNumber">PR number for the <see cref="ChannelKind.Pr"/> channel.</param>
/// <param name="VersionOverride">
/// Optional <c>ASPIRE_CLI_VERSION</c> value. Most scenarios set this to a
/// synthetic version (e.g. <c>9.6.0-staging.20260605.1</c>) so the CLI's
/// surfaces report the persona we're testing.
/// </param>
/// <param name="CommitOverride">Optional <c>ASPIRE_CLI_COMMIT</c> value.</param>
/// <param name="BuildPackagesBeforeLaunch">
/// When true, the preparer shells out to <c>./build.sh --pack</c> before the
/// terminal opens so the local NuGet proxy has fresh packages to overlay.
/// </param>
/// <param name="PackageVersionSuffix">
/// VersionSuffix passed to the build script. Required when
/// <see cref="BuildPackagesBeforeLaunch"/> is true; the preparer does not
/// fabricate a default at this layer (scenarios derive deterministic
/// suffixes so reruns are repeatable).
/// </param>
/// <param name="IncludeNativeBuild">
/// When false, the build runs with <c>/p:SkipNativeBuild=true</c>. Scenarios
/// usually leave this false; the multi-minute AOT cost is only justified
/// when dogfooding native-build-specific changes to the CLI binary.
/// </param>
/// <param name="UseLocalNuGetProxy">
/// When true, the preparer starts a per-session <c>DogfoodingNuGetServer</c>
/// and points the CLI at it via <c>ASPIRE_CLI_NUGET_SERVICE_INDEX</c>.
/// </param>
/// <param name="LocalPackageSourceDir">
/// Directory of <c>.nupkg</c> files the proxy overlays. Null defaults to
/// <c>artifacts/packages/Debug/Shipping/</c> under the repo root.
/// </param>
/// <param name="NuGetServiceIndexOverride">
/// Manual override for <c>ASPIRE_CLI_NUGET_SERVICE_INDEX</c> when not using
/// the local proxy. Most scenarios leave this null.
/// </param>
internal sealed record ScenarioPlan(
    ChannelKind Channel,
    int? PrNumber,
    string? VersionOverride,
    string? CommitOverride,
    bool BuildPackagesBeforeLaunch,
    string? PackageVersionSuffix,
    bool IncludeNativeBuild,
    bool UseLocalNuGetProxy,
    string? LocalPackageSourceDir,
    string? NuGetServiceIndexOverride);
