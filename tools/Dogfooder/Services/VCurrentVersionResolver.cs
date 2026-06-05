// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Resolves the version string the "Reproduce vCurrent" scenarios should
/// stamp onto built packages. "vCurrent" means the most recent stable
/// release shipped to nuget.org — which is NOT what
/// <c>eng/Versions.props</c> exposes (that file carries the in-development
/// version of whatever branch you're on, e.g. <c>13.5.0</c> on main while
/// <c>13.4.2</c> is the actual released vCurrent).
/// </summary>
/// <remarks>
/// Used by <c>ReproVCurrentLocalScenario</c> to (a) drive the persona the
/// CLI claims via <c>ASPIRE_CLI_VERSION</c>, and (b) drive the
/// <c>VersionPrefix</c> the build is invoked with so the produced
/// <c>.nupkg</c> files actually carry the released version number. Without
/// the latter the local build stamps packages with the in-development
/// version, the proxy serves them, but the CLI ignores them because the
/// search response advertises a version it doesn't expect for its persona.
/// </remarks>
internal interface IVCurrentVersionResolver
{
    /// <summary>
    /// Returns the latest stable <c>Aspire.Hosting</c> version published to
    /// nuget.org. Result is cached for the life of the resolver to avoid
    /// repeated nuget.org round-trips on TUI re-renders. <c>null</c> when
    /// the probe failed (offline, transient nuget.org outage) — callers
    /// should fall back to a sensible default and surface the fallback in
    /// the preparation log.
    /// </summary>
    Task<string?> GetLatestStableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Most recently resolved value, or null when the probe has not
    /// completed (yet, or it failed). Exposed as a synchronous snapshot so
    /// scenario <c>Build()</c> methods — which must be synchronous because
    /// they run during UI form submission — can read the value without
    /// blocking the render loop. Callers should treat null as "fall back
    /// to whatever <c>eng/Versions.props</c> says" and ideally surface the
    /// fallback in their preparation log.
    /// </summary>
    string? LatestStableOrNull { get; }
}

internal sealed class VCurrentVersionResolver : IVCurrentVersionResolver, IDisposable
{
    public VCurrentVersionResolver()
    {
        _http = new HttpClient
        {
            // Probe is one tiny JSON document; 10s is plenty even on a
            // slow link, and fails fast when nuget.org is unreachable.
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private bool _resolved;

    public string? LatestStableOrNull => Volatile.Read(ref _cached);

    public async Task<string?> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _cached;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolved)
            {
                return _cached;
            }
            _cached = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _resolved, true);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        // Flat-container "versions" index is the cheapest authoritative
        // listing on the NuGet v3 protocol — see
        // https://learn.microsoft.com/nuget/api/package-base-address-resource#enumerate-package-versions.
        // We use Aspire.Hosting (not Aspire.Cli) as the canonical "what's
        // the current Aspire release" probe because Aspire.Cli ships out
        // of band on a different cadence (npm + binary distributions),
        // while Aspire.Hosting tracks the release branch one-to-one.
        const string Url = "https://api.nuget.org/v3-flatcontainer/aspire.hosting/index.json";
        try
        {
            using var resp = await _http.GetAsync(Url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(stream) is not JsonObject obj || obj["versions"] is not JsonArray arr)
            {
                return null;
            }

            // Pick the highest stable (non-prerelease) version. nuget.org
            // sorts the array oldest→newest, but the contract doesn't
            // strictly guarantee it; doing a real comparison sidesteps the
            // brittleness. NuGet semver allows a dash in pre-release
            // labels (e.g. "13.5.0-preview.1") — we exclude those because
            // "vCurrent" means a shipped GA build.
            (int major, int minor, int patch, string raw)? best = null;
            foreach (var node in arr)
            {
                if (node?.GetValue<string>() is not { } v || v.Contains('-', StringComparison.Ordinal))
                {
                    continue;
                }
                if (!TryParseSimpleSemver(v, out var major, out var minor, out var patch))
                {
                    continue;
                }
                if (best is null
                    || (major, minor, patch).CompareTo((best.Value.major, best.Value.minor, best.Value.patch)) > 0)
                {
                    best = (major, minor, patch, v);
                }
            }
            return best?.raw;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseSimpleSemver(string s, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var parts = s.Split('.');
        return parts.Length == 3
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
