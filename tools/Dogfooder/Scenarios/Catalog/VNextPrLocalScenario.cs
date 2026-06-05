// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Scenarios.Catalog;

/// <summary>
/// Simulate a PR build locally — the CLI reports itself as <c>pr-{N}</c>
/// channel, packages are stamped with a PR-bound version suffix, and
/// restores go through the local proxy so the PR-built bits actually win.
/// </summary>
/// <remarks>
/// The only scenario in the catalog that requires an explicit user input
/// (the PR number). The version stamping follows the same shape the real
/// PR build infrastructure uses so consumers of the proxy see the version
/// strings they'd see when consuming the PR feed.
/// </remarks>
internal sealed class VNextPrLocalScenario : IDogfoodScenario
{
    public VNextPrLocalScenario(RepoVersionInfo repoVersion)
    {
        _repoVersion = repoVersion;
    }

    private readonly RepoVersionInfo _repoVersion;

    public string Id => "vnext-pr-local";
    public string DisplayName => "vNext PR + Local Packages";
    public string Description =>
        "Simulate a PR build locally. Builds packages with a PR-bound "
        + "version suffix and routes restores through the local proxy so "
        + "the PR bits win over published packages.";

    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = new[]
    {
        new ScenarioInputSpec("prNumber", "PR number", Placeholder: "e.g. 1234", Required: true),
    };

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        // PrNumber is required by the input spec, but Build is defensive so
        // the form can submit partial state without throwing — the UI surfaces
        // PrNumber == null clearly in the preparation log.
        int? prNumber = null;
        if (inputs.TryGetValue("prNumber", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            prNumber = parsed;
        }

        // Suffix shape echoes the official PR feed convention so users see
        // version strings consistent with the real PR build.
        var suffix = prNumber is int n
            ? string.Format(CultureInfo.InvariantCulture, "pr.{0}.{1:yyyyMMdd}.1", n, DateTime.UtcNow)
            : string.Format(CultureInfo.InvariantCulture, "pr.MISSING.{0:yyyyMMdd}.1", DateTime.UtcNow);

        // Use the current repo version as the base — same scheme the PR
        // build pipeline uses (it bumps off whatever's on main, not vCurrent).
        var baseVersion = _repoVersion.CurrentVersionString;

        return new ScenarioPlan(
            Channel: ChannelKind.Pr,
            PrNumber: prNumber,
            VersionOverride: $"{baseVersion}-{suffix}",
            CommitOverride: null,
            BuildPackagesBeforeLaunch: true,
            PackageVersionSuffix: suffix,
            IncludeNativeBuild: false,
            UseLocalNuGetProxy: true,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null);
    }
}
