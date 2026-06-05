// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Reproduce the currently-released vCurrent CLI and packages from local
/// source. Useful when triaging an issue against the latest release: rebuild
/// exactly what shipped, serve those binaries via the local NuGet proxy,
/// then poke at the reproduced behaviour with the option to start patching.
/// </summary>
internal sealed class ReproVCurrentLocalScenario : IDogfoodScenario
{
    public ReproVCurrentLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
    }

    private readonly RepoVersionInfo _repoVersion;

    public string Id => "repro-vcurrent-local";
    public string DisplayName => "Reproduce vCurrent CLI + Local Packages";
    public string Description =>
        $"Rebuild the currently-released Aspire ({_repoVersion.CurrentVersionString}) "
        + "from local source. Restore via the local NuGet proxy so any package "
        + "the CLI restores comes from the build artifacts rather than nuget.org.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Stable,
            PrNumber: null,
            // Reproduce exactly the released stable version string, so the
            // CLI surfaces report the persona we're attempting to reproduce.
            VersionOverride: _repoVersion.CurrentVersionString,
            CommitOverride: null,
            // Build with NO version suffix so the resulting packages are
            // exactly the stable version of vCurrent (e.g. 13.5.0).
            BuildPackagesBeforeLaunch: true,
            PackageVersionSuffix: "",
            IncludeNativeBuild: false,
            UseLocalNuGetProxy: true,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null);
    }
}
