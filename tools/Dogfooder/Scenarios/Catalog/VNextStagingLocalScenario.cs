// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Simulate a staging (release-candidate) build locally. The CLI reports as
/// the staging channel and packages are stamped with a staging-shaped
/// pre-release suffix — useful for previewing the experience a staging
/// consumer would have before we cut the matching real staging build.
/// </summary>
internal sealed class VNextStagingLocalScenario : IDogfoodScenario
{
    public VNextStagingLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
        _packageVersionSuffix = string.Format(
            CultureInfo.InvariantCulture,
            "staging.{0:yyyyMMdd}.1",
            DateTime.UtcNow);
    }

    private readonly RepoVersionInfo _repoVersion;
    private readonly string _packageVersionSuffix;

    public string Id => "vnext-staging-local";
    public string DisplayName => "vNext Staging + Local Packages";
    public string Description =>
        $"Simulate a staging (release-candidate) build of {_repoVersion.NextMinorVersionString}. "
        + "Builds local packages with a staging-shaped suffix and routes "
        + "restores through the local proxy.";
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        return new ScenarioPlan(
            Channel: ChannelKind.Staging,
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
