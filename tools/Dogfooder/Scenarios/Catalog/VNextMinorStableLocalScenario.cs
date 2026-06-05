// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Build local packages stamped as the synthetic <em>next-minor</em> stable
/// (current major.minor+1.0) and route restores through the local proxy.
/// Lets us preview what the next minor release will look like end-to-end.
/// </summary>
internal sealed class VNextMinorStableLocalScenario : IDogfoodScenario
{
    public VNextMinorStableLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
        _packageVersionSuffix = string.Format(
            CultureInfo.InvariantCulture,
            "vnextminor.{0:yyyyMMdd}.1",
            DateTime.UtcNow);
    }

    private readonly RepoVersionInfo _repoVersion;
    // Built once at scenario-singleton construction so reruns within the same
    // app session produce the same suffix and the local proxy doesn't end up
    // serving two competing builds of the same id.
    private readonly string _packageVersionSuffix;

    public string Id => "vnext-minor-stable-local";
    public string DisplayName => "vNext (Minor) Stable + Local Packages";
    public string Description =>
        $"Build local packages as {_repoVersion.NextMinorVersionString}-{_packageVersionSuffix} "
        + "and present them via the local NuGet proxy. Preview the next minor "
        + "stable release end-to-end against your tree.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Stable,
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
