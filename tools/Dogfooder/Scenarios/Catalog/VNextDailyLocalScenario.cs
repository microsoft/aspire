// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Simulate a daily build locally. Same shape as the real daily channel: an
/// alpha-suffixed bump on top of vNext's current minor, routed through the
/// local proxy. Useful for understanding how daily consumers will see the
/// current main branch.
/// </summary>
internal sealed class VNextDailyLocalScenario : IDogfoodScenario
{
    public VNextDailyLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
        _packageVersionSuffix = string.Format(
            CultureInfo.InvariantCulture,
            "daily.{0:yyyyMMdd}.1",
            DateTime.UtcNow);
    }

    private readonly RepoVersionInfo _repoVersion;
    private readonly string _packageVersionSuffix;

    public string Id => "vnext-daily-local";
    public string DisplayName => "vNext Daily + Local Packages";
    public string Description =>
        $"Simulate a daily build of {_repoVersion.NextMinorVersionString}. "
        + "Builds local packages with a daily-shaped suffix and routes "
        + "restores through the local proxy so the daily bits win.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Daily,
            PrNumber: null,
            VersionOverride: $"{_repoVersion.NextMinorVersionString}-{_packageVersionSuffix}",
            CommitOverride: null,
            BuildPackagesBeforeLaunch: true,
            PackageVersionSuffix: _packageVersionSuffix,
            IncludeNativeBuild: false,
            UseLocalNuGetProxy: true,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null);
    }
}
