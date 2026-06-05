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
        // Authoritative "what's vCurrent" answer comes from nuget.org and
        // must be resolved before we build packages — otherwise we'd stamp
        // them with the in-development version from eng/Versions.props
        // (e.g. 13.5.0 on the main branch) instead of the actually-shipped
        // vCurrent (13.4.2). That mismatch is silent and corrosive: the CLI
        // identity becomes 13.5.0, the packages become 13.5.0, and `aspire
        // new` then offers a 13.5.0 template that's never been released.
        //
        // Block on the probe with a small timeout. The probe is started
        // fire-and-forget in Program.cs at TUI launch, so by the time the
        // user picks this scenario it's usually already cached and this
        // wait returns instantly. The 10s ceiling is the same ceiling the
        // resolver's HttpClient uses, so we never wait longer than a
        // single nuget.org round-trip would.
        var snapshot = _vCurrentResolver.LatestStableOrNull;
        if (snapshot is null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                snapshot = _vCurrentResolver.GetLatestStableAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Surface as InvalidOperationException so the form layer
                // shows a real error message instead of silently using a
                // wrong fallback. Callers explicitly chose this scenario
                // because they want to reproduce the released bits — if we
                // can't tell them what those are, refusing to run is the
                // right call.
                throw new InvalidOperationException(
                    "Could not resolve the current released Aspire version from nuget.org within 10s. "
                    + "Check network connectivity and retry. (Probe URL: api.nuget.org/v3-flatcontainer/aspire.hosting/index.json)");
            }
        }
        var vCurrent = snapshot
            ?? throw new InvalidOperationException(
                "nuget.org returned no stable Aspire.Hosting versions. This is unexpected — "
                + "check https://www.nuget.org/packages/Aspire.Hosting.");
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
