// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Build local packages stamped as the synthetic next <em>patch</em> stable
/// (current major.minor.patch+1) and route restores through the local proxy.
/// Mirrors the hotfix shape: a small bump on top of the latest released line.
/// </summary>
internal sealed class VNextHotfixStableLocalScenario : IDogfoodScenario
{
    public VNextHotfixStableLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
        _packageVersionSuffix = string.Format(
            CultureInfo.InvariantCulture,
            "vnexthotfix.{0:yyyyMMdd}.1",
            DateTime.UtcNow);
    }

    private readonly RepoVersionInfo _repoVersion;
    private readonly string _packageVersionSuffix;

    public string Id => "vnext-hotfix-stable-local";
    public string DisplayName => "vNext (Hotfix) Stable + Local Packages";
    public string Description =>
        $"Build local packages as {_repoVersion.NextPatchVersionString}-{_packageVersionSuffix} "
        + "and present them via the local NuGet proxy. Preview a hotfix "
        + "release end-to-end against your tree.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Stable,
            PrNumber: null,
            VersionOverride: $"{_repoVersion.NextPatchVersionString}-{_packageVersionSuffix}",
            CommitOverride: null,
            BuildPackagesBeforeLaunch: true,
            PackageVersionSuffix: _packageVersionSuffix,
            IncludeNativeBuild: false,
            UseLocalNuGetProxy: true,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null);
    }
}
