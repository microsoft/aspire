// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Reproduce the currently-released vCurrent CLI from local source while
/// restoring packages from nuget.org. Use when triaging a CLI-only bug —
/// avoids the multi-minute package build, but still gives you a local CLI
/// you can patch and re-run.
/// </summary>
internal sealed class ReproVCurrentPublishedScenario : IDogfoodScenario
{
    public ReproVCurrentPublishedScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
    }

    private readonly RepoVersionInfo _repoVersion;

    public string Id => "repro-vcurrent-published";
    public string DisplayName => "Reproduce vCurrent CLI + Published Packages";
    public string Description =>
        $"Rebuild only the CLI ({_repoVersion.CurrentVersionString}) from local "
        + "source. Package restore goes to nuget.org as released users would "
        + "experience it. Fast path for CLI-only investigations.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Stable,
            PrNumber: null,
            VersionOverride: _repoVersion.CurrentVersionString,
            CommitOverride: null,
            // No package build — only the CLI binary is needed and that is
            // produced by the ordinary `./build.sh` we expect the user to
            // have already run (or the dotnet run from the local source tree).
            BuildPackagesBeforeLaunch: false,
            PackageVersionSuffix: null,
            IncludeNativeBuild: false,
            // Do NOT route restores through the proxy — the whole point of
            // this scenario is to hit the real published feed.
            UseLocalNuGetProxy: false,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null);
    }
}
