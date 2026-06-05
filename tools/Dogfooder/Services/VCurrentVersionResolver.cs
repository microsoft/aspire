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

    /// <summary>
    /// Returns the GitHub commit SHA the <paramref name="version"/> tag
    /// (<c>v{version}</c>) points at in <c>microsoft/aspire</c>. Used to
    /// stamp <c>ASPIRE_CLI_COMMIT</c> so the CLI's identity surface
    /// reports the same commit the shipped vCurrent was built from
    /// instead of whatever the local in-development branch points at.
    /// Cached per-version; <c>null</c> when the tag isn't found or the
    /// probe fails (offline, rate-limited).
    /// </summary>
    Task<string?> GetCommitShaAsync(string version, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronous snapshot of <see cref="GetCommitShaAsync"/> for use
    /// inside scenario <c>Build()</c> overrides. Null when the probe has
    /// not completed yet or failed; callers should fall through to "no
    /// commit override" rather than blocking.
    /// </summary>
    string? CommitShaOrNull(string version);
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
        // GitHub's REST API requires a User-Agent on every request; the
        // commit-SHA probe will 403 without one. Aspire-Dogfooder is a
        // descriptive UA that lets GitHub correlate any rate-limit logs
        // with the tool rather than to "anonymous HttpClient".
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Aspire-Dogfooder/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private bool _resolved;
    // Commit-SHA cache is keyed by version because multiple scenarios
    // could ask for different (e.g. PR-build) versions in the same run.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _commitByVersion = new(StringComparer.OrdinalIgnoreCase);

    public string? LatestStableOrNull => Volatile.Read(ref _cached);

    public string? CommitShaOrNull(string version)
        => _commitByVersion.TryGetValue(version, out var sha) ? sha : null;

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

    public async Task<string?> GetCommitShaAsync(string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        if (_commitByVersion.TryGetValue(version, out var existing))
        {
            return existing;
        }
        var sha = await ProbeCommitShaAsync(version, cancellationToken).ConfigureAwait(false);
        // Memoise even when the probe returned null so we don't hammer
        // GitHub on every TUI re-render. A stale "not found" is fine —
        // the user can restart the Dogfooder to retry.
        _commitByVersion[version] = sha;
        return sha;
    }

    private async Task<string?> ProbeCommitShaAsync(string version, CancellationToken cancellationToken)
    {
        // GitHub resolves any ref-like string (including tag names) to a
        // commit via /repos/{owner}/{repo}/commits/{ref}. The released
        // Aspire tag convention is `v{version}` (e.g. v13.4.2), confirmed
        // by https://github.com/dotnet/aspire/releases.
        //
        // Why dotnet/aspire rather than microsoft/aspire: the repo was
        // renamed from dotnet to microsoft but release tags continue to
        // resolve under both via GitHub's transparent redirect. Probing
        // the canonical microsoft/aspire endpoint avoids the redirect
        // hop and matches `git ls-remote` behaviour.
        var url = $"https://api.github.com/repos/microsoft/aspire/commits/v{version}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(stream) is not JsonObject obj)
            {
                return null;
            }
            return obj["sha"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
