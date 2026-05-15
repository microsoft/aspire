// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IInstallationDiscovery"/>. The self-describe path
/// composes data already available in-process (channel from
/// <see cref="IIdentityChannelReader"/>, version from
/// <see cref="VersionHelper.GetDefaultTemplateVersion"/>, route from the
/// running binary's sidecar) so it is cheap and side-effect-free.
/// </summary>
/// <remarks>
/// <para>
/// The <c>--all</c> path walks three discovery sources:
/// </para>
/// <list type="number">
///   <item>The user's <c>$PATH</c> looking for an <c>aspire</c> /
///   <c>aspire.exe</c> entry.</item>
///   <item>Well-known release- and PR-script install prefixes under
///   <c>~/.aspire</c>.</item>
///   <item>The dotnet-tool store under <c>~/.dotnet/tools/.store/aspire.cli/</c>
///   (because the on-PATH dotnet-tool shim has no sidecar; only the real
///   binary inside the store carries one).</item>
/// </list>
/// <para>
/// A trust gate enforces that we only spawn peers whose binary directory
/// contains a readable install-route sidecar with a known <c>source</c>.
/// Untrusted PATH discoveries are listed with
/// <see cref="InstallationInfoStatus.NotProbed"/> and never executed.
/// </para>
/// </remarks>
internal sealed class InstallationDiscovery : IInstallationDiscovery
{
    private readonly IIdentityChannelReader _channelReader;
    private readonly IInstallSidecarReader _sidecarReader;
    private readonly IPeerInstallProbe _peerProbe;
    private readonly ILogger<InstallationDiscovery> _logger;

    public InstallationDiscovery(
        IIdentityChannelReader channelReader,
        IInstallSidecarReader sidecarReader,
        IPeerInstallProbe peerProbe,
        ILogger<InstallationDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(channelReader);
        ArgumentNullException.ThrowIfNull(sidecarReader);
        ArgumentNullException.ThrowIfNull(peerProbe);
        ArgumentNullException.ThrowIfNull(logger);

        _channelReader = channelReader;
        _sidecarReader = sidecarReader;
        _peerProbe = peerProbe;
        _logger = logger;
    }

    /// <inheritdoc />
    public InstallationInfo DescribeSelf()
    {
        var processPath = Environment.ProcessPath;
        var canonicalPath = ResolveCanonicalPath(processPath, _logger);
        var binaryDir = !string.IsNullOrEmpty(canonicalPath) ? Path.GetDirectoryName(canonicalPath) : null;

        var sidecar = !string.IsNullOrEmpty(binaryDir) ? _sidecarReader.TryRead(binaryDir) : null;
        // Use the wire string from the parsed source so callers see the same
        // identifier the install scripts wrote, not the C# enum name. For
        // sidecars with an unrecognized source value we surface the raw
        // string so users see "(unknown: future-route)" rather than nothing.
        var route = sidecar?.Source.ToWireString() ?? sidecar?.RawSource;

        return new InstallationInfo
        {
            Path = processPath ?? string.Empty,
            CanonicalPath = canonicalPath,
            Version = VersionHelper.GetDefaultTemplateVersion(),
            Channel = TryReadChannel(),
            Route = route,
            IsOnPath = IsOnPathSelf(canonicalPath, _logger),
            Status = InstallationInfoStatus.Ok,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var self = DescribeSelf();
        _logger.LogDebug(
            "Discovery: starting walk. self.Path='{SelfPath}', self.Canonical='{SelfCanonical}', HOME='{Home}'.",
            self.Path,
            self.CanonicalPath ?? "(null)",
            HomeDirectoryResolver.GetUserHomeDirectory());

        var results = new List<InstallationInfo> { self };
        // Deduplicate by canonical path (case-insensitive on Windows). The
        // running CLI is always the first row, so peers that resolve to
        // the same canonical path are silently dropped.
        var seen = new HashSet<string>(
            self.CanonicalPath is { Length: > 0 } sp ? [sp] : [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var pathHit = FindFirstAspireOnPath(_logger);
        if (pathHit is null)
        {
            _logger.LogDebug("Discovery: no 'aspire' binary found on $PATH.");
        }
        else
        {
            _logger.LogDebug(
                "Discovery: $PATH first match: '{Path}' (canonical: '{Canonical}').",
                pathHit.OriginalPath, pathHit.CanonicalPath);
        }

        var candidateCount = 0;
        foreach (var candidate in EnumerateDiscoveryCandidates(pathHit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateCount++;

            _logger.LogDebug(
                "Discovery: considering candidate #{Index} '{Path}' (origin: {Origin}).",
                candidateCount, candidate.BinaryPath, candidate.Origin);

            var canonical = ResolveCanonicalPath(candidate.BinaryPath, _logger);
            if (string.IsNullOrEmpty(canonical))
            {
                _logger.LogDebug(
                    "Discovery: skipping candidate '{Candidate}' (origin: {Origin}) — could not resolve a canonical path; treating as not a real install.",
                    candidate.BinaryPath, candidate.Origin);
                continue;
            }
            if (!seen.Add(canonical))
            {
                _logger.LogDebug(
                    "Discovery: skipping duplicate of '{Canonical}' found via {Origin} at '{Candidate}'.",
                    canonical, candidate.Origin, candidate.BinaryPath);
                continue;
            }

            var binaryDir = Path.GetDirectoryName(canonical);
            var sidecar = !string.IsNullOrEmpty(binaryDir) ? _sidecarReader.TryRead(binaryDir) : null;

            // Trust gate (RD-2): we only spawn peers that carry a readable
            // install-route sidecar with a known source. Untrusted PATH
            // hits become notProbed rows so users still see they exist
            // but we never execute them. Logged so a developer running
            // with --log-level debug can see exactly why a candidate
            // didn't get classified as a real install.
            if (sidecar is null)
            {
                _logger.LogDebug(
                    "Discovery: candidate '{Canonical}' (origin: {Origin}) has no .aspire-install.json sidecar at '{BinaryDir}' — treating as not-probed (trust gate).",
                    canonical, candidate.Origin, binaryDir);
                results.Add(new InstallationInfo
                {
                    Path = candidate.BinaryPath,
                    CanonicalPath = canonical,
                    Status = InstallationInfoStatus.NotProbed,
                    StatusReason = "No install-route sidecar found (trust gate).",
                });
                continue;
            }
            if (sidecar.Source == InstallSource.Unknown)
            {
                _logger.LogDebug(
                    "Discovery: candidate '{Canonical}' (origin: {Origin}) has sidecar at '{SidecarPath}' but its source value '{RawSource}' is not a known install route — treating as not-probed (trust gate).",
                    canonical, candidate.Origin, sidecar.SidecarPath, sidecar.RawSource ?? "(empty)");
                results.Add(new InstallationInfo
                {
                    Path = candidate.BinaryPath,
                    CanonicalPath = canonical,
                    Status = InstallationInfoStatus.NotProbed,
                    StatusReason = $"Sidecar reports unknown source '{sidecar.RawSource ?? "(empty)"}' (trust gate).",
                });
                continue;
            }

            var probe = await _peerProbe.ProbeAsync(canonical, cancellationToken).ConfigureAwait(false);
            switch (probe)
            {
                case PeerProbeResult.Ok ok:
                    // Preserve the original discovered path for display and
                    // canonical path for identity. Overlay the route from
                    // the LOCAL sidecar so older peers using the
                    // --version fallback (which can't report route) still
                    // surface the install route we already know about.
                    // Also derive the channel for PR-route installs from
                    // the directory layout — the channel is structurally
                    // `pr-<N>` for a PR install, so we can show it even
                    // when the older peer didn't report it.
                    var route = ok.Info.Route ?? sidecar.Source.ToWireString();
                    var channel = ok.Info.Channel;
                    if (string.IsNullOrEmpty(channel) && sidecar.Source == InstallSource.Pr)
                    {
                        channel = TryDerivePrChannel(canonical);
                    }

                    results.Add(ok.Info with
                    {
                        Path = candidate.BinaryPath,
                        CanonicalPath = canonical,
                        Route = route,
                        Channel = channel,
                        IsOnPath = canonical.Equals(pathHit?.CanonicalPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                    });
                    break;
                case PeerProbeResult.Failed failed:
                    _logger.LogDebug(
                        "Discovery: candidate '{Canonical}' (origin: {Origin}, route: {Route}) failed peer probe: {Reason}.",
                        canonical, candidate.Origin, sidecar.Source.ToWireString(), failed.Reason);
                    results.Add(new InstallationInfo
                    {
                        Path = candidate.BinaryPath,
                        CanonicalPath = canonical,
                        Route = sidecar.Source.ToWireString(),
                        Status = InstallationInfoStatus.NotProbed,
                        StatusReason = failed.Reason,
                    });
                    break;
            }
        }

        _logger.LogDebug(
            "Discovery: walk complete. Considered {Considered} candidate(s); produced {Total} row(s) total (including self).",
            candidateCount, results.Count);

        return results;
    }

    /// <summary>
    /// Derives the <c>pr-&lt;N&gt;</c> identity channel for a PR-route install
    /// from its on-disk path. The PR install layout is, by convention,
    /// <c>&lt;root&gt;/dogfood/pr-&lt;N&gt;/bin/aspire</c> (or with a
    /// <c>.exe</c>); this method walks up two directories from the binary
    /// and returns the second-to-last component when it matches that
    /// shape. For custom-prefix PR installs (<c>--install-path</c> with a
    /// non-default layout) the lookup returns <see langword="null"/> and
    /// the row falls back to <c>(unknown)</c> for channel.
    /// </summary>
    /// <remarks>
    /// This derivation is purely cosmetic for the user-facing table: it
    /// fills in the channel column when the older peer at the discovered
    /// path has no surface to report its baked <c>AspireCliChannel</c>.
    /// It is not used for any decision-making logic (extract dir, hive
    /// resolution, etc.) — those continue to use the sidecar source.
    /// </remarks>
    internal static string? TryDerivePrChannel(string canonicalBinaryPath)
    {
        // canonicalBinaryPath: <root>/dogfood/pr-<N>/bin/aspire[.exe]
        //    parent           = <root>/dogfood/pr-<N>/bin
        //    grandparent      = <root>/dogfood/pr-<N>           ← we want the basename
        //    great-grandparent= <root>/dogfood                   ← which must equal "dogfood"
        var bin = Path.GetDirectoryName(canonicalBinaryPath);
        if (string.IsNullOrEmpty(bin))
        {
            return null;
        }

        var prDir = Path.GetDirectoryName(bin);
        if (string.IsNullOrEmpty(prDir))
        {
            return null;
        }

        var dogfoodDir = Path.GetDirectoryName(prDir);
        if (string.IsNullOrEmpty(dogfoodDir) ||
            !string.Equals(Path.GetFileName(dogfoodDir), "dogfood", StringComparison.Ordinal))
        {
            return null;
        }

        var label = Path.GetFileName(prDir);
        if (string.IsNullOrEmpty(label) || !label.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Validate the suffix is digits-only so e.g. `pr-foo` from a manual
        // prefix doesn't get surfaced as an identity channel.
        var suffix = label.AsSpan(3);
        if (suffix.IsEmpty || suffix.ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }

        return label;
    }

    /// <summary>
    /// Resolves any symlinks in <paramref name="processPath"/> so that two
    /// PATH entries pointing at the same backing file produce the same
    /// canonical identifier. Mirrors the symlink resolution that
    /// <see cref="Bundles.BundleService"/> uses for sidecar lookup so
    /// <c>info</c> and <c>BundleService</c> agree on identity.
    /// </summary>
    private static string? ResolveCanonicalPath(string? processPath, ILogger? logger)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        try
        {
            var resolved = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
            return resolved?.FullName ?? Path.GetFullPath(processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or System.Security.SecurityException)
        {
            logger?.LogDebug(ex, "Could not resolve symlink target for {Path}; using the normalized path.", processPath);
            return Path.GetFullPath(processPath);
        }
    }

    private string? TryReadChannel()
    {
        try
        {
            return _channelReader.ReadChannel();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same defensive posture as doctor: a misconfigured dev build
            // with no AspireCliChannel assembly metadata must not break
            // aspire info.
            _logger.LogDebug(ex, "Could not read identity channel for InstallationDiscovery.");
            return null;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the canonical resolution of
    /// <c>aspire</c> on the current <c>$PATH</c> matches <paramref name="canonicalSelfPath"/>.
    /// </summary>
    private static bool IsOnPathSelf(string? canonicalSelfPath, ILogger? logger)
    {
        if (string.IsNullOrEmpty(canonicalSelfPath))
        {
            return false;
        }

        var first = FindFirstAspireOnPath(logger);
        if (first is null)
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return comparer.Equals(first.CanonicalPath, canonicalSelfPath);
    }

    /// <summary>
    /// Walks <c>$PATH</c> looking for the first <c>aspire</c> /
    /// <c>aspire.exe</c> binary the shell would resolve. Returns
    /// <see langword="null"/> when nothing is found.
    /// </summary>
    private static PathHit? FindFirstAspireOnPath(ILogger? logger)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var binaryNames = OperatingSystem.IsWindows() ? new[] { "aspire.exe", "aspire" } : new[] { "aspire" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in binaryNames)
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate))
                {
                    continue;
                }
                var canonical = ResolveCanonicalPath(candidate, logger);
                if (!string.IsNullOrEmpty(canonical))
                {
                    return new PathHit(candidate, canonical);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Yields discovery candidates in priority order:
    /// <c>$PATH</c> hit (if any), well-known prefixes, dotnet-tool store.
    /// Each candidate carries an <c>Origin</c> tag identifying which
    /// discovery source produced it so the rejection logs can pinpoint
    /// the responsible walk (e.g. "dogfood: directory exists but bin/aspire
    /// is missing — was the install corrupted?").
    /// </summary>
    private IEnumerable<DiscoveryCandidate> EnumerateDiscoveryCandidates(PathHit? pathHit)
    {
        if (pathHit is not null)
        {
            yield return new DiscoveryCandidate(pathHit.OriginalPath, "$PATH");
        }

        var home = HomeDirectoryResolver.GetUserHomeDirectory();
        if (string.IsNullOrEmpty(home))
        {
            _logger.LogDebug("Discovery: no user home directory available; skipping well-known prefix walk and dotnet-tool store probe.");
            yield break;
        }

        // Release-script default. We always check for the binary at the
        // canonical location even when the parent dir is absent, because
        // File.Exists short-circuits cleanly.
        var releaseDir = Path.Combine(home, ".aspire", "bin");
        var releaseBinary = Path.Combine(releaseDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        if (File.Exists(releaseBinary))
        {
            _logger.LogDebug("Discovery: release prefix walk yielded '{Binary}'.", releaseBinary);
            yield return new DiscoveryCandidate(releaseBinary, "well-known release prefix");
        }
        else if (Directory.Exists(releaseDir))
        {
            // Bin dir exists but no `aspire` inside it — likely a partially
            // removed install or a third-party `~/.aspire/bin` use. Worth
            // surfacing in debug logs so the user can correlate with their
            // expectation.
            _logger.LogDebug(
                "Discovery: release prefix directory '{ReleaseDir}' exists but does not contain an 'aspire' binary — not classifying as a real install.",
                releaseDir);
        }
        else
        {
            _logger.LogDebug("Discovery: release prefix '{ReleaseDir}' does not exist; skipping.", releaseDir);
        }

        // PR-script default: ~/.aspire/dogfood/pr-*/bin/aspire[.exe].
        var dogfoodRoot = Path.Combine(home, ".aspire", "dogfood");
        if (Directory.Exists(dogfoodRoot))
        {
            var subdirCount = 0;
            foreach (var prDir in EnumerateDirectoriesSafe(dogfoodRoot))
            {
                subdirCount++;
                var binDir = Path.Combine(prDir, "bin");
                var binary = Path.Combine(binDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
                if (File.Exists(binary))
                {
                    _logger.LogDebug("Discovery: dogfood walk yielded '{Binary}'.", binary);
                    yield return new DiscoveryCandidate(binary, "dogfood prefix");
                }
                else
                {
                    // A dogfood pr-N directory without bin/aspire is most
                    // commonly a stale leftover from a failed install or a
                    // partial uninstall. Log so the user can see exactly
                    // which dir was expected to host an install but didn't.
                    _logger.LogDebug(
                        "Discovery: dogfood directory '{PrDir}' exists but does not contain a '{Bin}/aspire' binary — not classifying as a real install.",
                        prDir, "bin");
                }
            }
            if (subdirCount == 0)
            {
                _logger.LogDebug("Discovery: dogfood root '{DogfoodRoot}' exists but contains no subdirectories.", dogfoodRoot);
            }
        }
        else
        {
            _logger.LogDebug("Discovery: dogfood root '{DogfoodRoot}' does not exist; skipping.", dogfoodRoot);
        }

        // Dotnet-tool store probe (RD-11 reuse). The shape is
        // ~/.dotnet/tools/.store/aspire.cli/<version>/aspire.cli/<version>/tools/<tfm>/<rid>/aspire[.exe].
        // We don't rebuild that whole path; we enumerate version dirs and
        // glob downward, which is robust to <version>, <tfm>, and <rid>
        // shifting in future packages.
        var toolStore = Path.Combine(home, ".dotnet", "tools", ".store", "aspire.cli");
        if (Directory.Exists(toolStore))
        {
            var binaryName = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
            // EnumerateFiles with SearchOption.AllDirectories is cheap
            // here because the .store tree is shallow and Aspire-owned;
            // we accept the breadth-first walk for code simplicity.
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(toolStore, binaryName, SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Discovery: failed to enumerate dotnet-tool store at '{ToolStore}'.", toolStore);
                matches = [];
            }
            var anyMatch = false;
            foreach (var match in matches)
            {
                anyMatch = true;
                _logger.LogDebug("Discovery: dotnet-tool store walk yielded '{Binary}'.", match);
                yield return new DiscoveryCandidate(match, "dotnet-tool store");
            }
            if (!anyMatch)
            {
                _logger.LogDebug(
                    "Discovery: dotnet-tool store '{ToolStore}' exists but contains no '{BinaryName}' binary — not classifying as a real install.",
                    toolStore, binaryName);
            }
        }
        else
        {
            _logger.LogDebug("Discovery: dotnet-tool store '{ToolStore}' does not exist; skipping.", toolStore);
        }
    }

    private IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Discovery: failed to enumerate directories under '{Root}'.", root);
            return [];
        }
    }

    private sealed record PathHit(string OriginalPath, string CanonicalPath);

    private sealed record DiscoveryCandidate(string BinaryPath, string Origin);
}
