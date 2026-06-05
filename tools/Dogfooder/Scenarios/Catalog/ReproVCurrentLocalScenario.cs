// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
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
    public ReproVCurrentLocalScenario(RepoVersionInfo repoVersion, IVCurrentVersionResolver vCurrentResolver)
    {
        _repoVersion = repoVersion;
        _vCurrentResolver = vCurrentResolver;
    }

    private readonly RepoVersionInfo _repoVersion;
    private readonly IVCurrentVersionResolver _vCurrentResolver;

    public string Id => "repro-vcurrent-local";
    public string DisplayName => "Reproduce vCurrent CLI + Local Packages";
    public string Description
    {
        get
        {
            // Prefer the value resolved from nuget.org because the branch's
            // eng/Versions.props carries the *next* version, not the
            // currently-shipped one. Fall back to the repo version when
            // the probe hasn't finished (or failed) so the form still
            // renders a useful description while offline.
            var v = _vCurrentResolver.LatestStableOrNull ?? _repoVersion.CurrentVersionString;
            return $"Rebuild the currently-released Aspire ({v}) "
                + "from local source. Restore via the local NuGet proxy so any package "
                + "the CLI restores comes from the build artifacts rather than nuget.org.";
        }
    }
    public IReadOnlyList<ScenarioInputSpec> Inputs { get; } = Array.Empty<ScenarioInputSpec>();

    public ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs)
    {
        _ = inputs;
        // Authoritative "what's vCurrent" answer comes from nuget.org. The
        // repo version is only a fallback for the offline case — it would
        // be wrong on any branch where the in-development version differs
        // from the latest released version (i.e. always, except briefly
        // right after a release).
        var vCurrent = _vCurrentResolver.LatestStableOrNull ?? _repoVersion.CurrentVersionString;
        // Best-effort commit SHA lookup. Null when the GitHub probe hasn't
        // completed yet (or failed) — in that case we deliberately do not
        // stamp ASPIRE_CLI_COMMIT and let the CLI fall back to whatever
        // the local build embedded. Stamping a wrong SHA would be worse
        // than stamping no SHA because diagnostics tools (eg. crash dumps)
        // would mis-attribute the run.
        var commitSha = _vCurrentResolver.CommitShaOrNull(vCurrent);
        return new ScenarioPlan(
            Channel: ChannelKind.Stable,
            PrNumber: null,
            // Reproduce exactly the released stable version string so the
            // CLI surfaces report the persona we're attempting to reproduce.
            VersionOverride: vCurrent,
            CommitOverride: commitSha,
            // Build with NO version suffix so the resulting packages are
            // exactly the stable version we're reproducing (e.g. 13.4.2).
            BuildPackagesBeforeLaunch: true,
            PackageVersionSuffix: "",
            IncludeNativeBuild: false,
            UseLocalNuGetProxy: true,
            LocalPackageSourceDir: null,
            NuGetServiceIndexOverride: null,
            // Stamp the build with vCurrent so the produced .nupkg files
            // actually carry the released version number rather than the
            // branch's in-development one (which would mismatch the
            // ASPIRE_CLI_VERSION persona we set above and cause restore
            // to ignore the local overlay).
            PackageVersionPrefix: vCurrent);
    }
}
