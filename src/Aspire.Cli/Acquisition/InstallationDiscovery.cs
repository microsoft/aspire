// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
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
/// The default discovery path asks ordered candidate sources for possible installs,
/// then centralizes canonicalization, deduplication, install metadata checks,
/// peer probing, and row shaping in this class.
/// </remarks>
internal sealed partial class InstallationDiscovery : IInstallationDiscovery
{
    private static readonly string s_aspireBinaryName = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";

    private readonly IIdentityChannelReader _channelReader;
    private readonly IInstallSidecarReader _sidecarReader;
    private readonly IPeerInstallProbe _peerProbe;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<InstallationDiscovery> _logger;
    private readonly IReadOnlyList<IInstallationCandidateSource> _candidateSources;

    public InstallationDiscovery(
        IIdentityChannelReader channelReader,
        IInstallSidecarReader sidecarReader,
        IPeerInstallProbe peerProbe,
        CliExecutionContext executionContext,
        ILogger<InstallationDiscovery> logger,
        IEnumerable<IInstallationCandidateSource>? candidateSources = null)
    {
        ArgumentNullException.ThrowIfNull(channelReader);
        ArgumentNullException.ThrowIfNull(sidecarReader);
        ArgumentNullException.ThrowIfNull(peerProbe);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);

        _channelReader = channelReader;
        _sidecarReader = sidecarReader;
        _peerProbe = peerProbe;
        _executionContext = executionContext;
        _logger = logger;
        var sources = candidateSources?.ToArray();
        _candidateSources = sources is { Length: > 0 } ? sources : CreateDefaultCandidateSources();
    }

    /// <inheritdoc />
    public InstallationInfo DescribeSelf()
        => DescribeSelf(Environment.ProcessPath, pathHits: null);

    /// <summary>
    /// Test seam for <see cref="DescribeSelf()"/>: lets tests substitute the
    /// running CLI's process path so we can exercise the firmlink-strip
    /// path under <see cref="CliPathHelper.ResolveSymlinkToFullPath(string?, ILogger?)"/>
    /// without manipulating <see cref="Environment.ProcessPath"/>.
    /// </summary>
    internal InstallationInfo DescribeSelf(string? processPath)
        => DescribeSelf(processPath, pathHits: null);

    private InstallationInfo DescribeSelf(string? processPath, IReadOnlyList<InstallationPathHit>? pathHits)
    {
        var canonicalPath = CliPathHelper.ResolveSymlinkToFullPath(processPath, _logger);
        var binaryDir = !string.IsNullOrEmpty(canonicalPath) ? Path.GetDirectoryName(canonicalPath) : null;

        var sidecar = !string.IsNullOrEmpty(binaryDir) && _sidecarReader.TryRead(binaryDir) is InstallSidecarReadResult.Ok selfSidecar
            ? selfSidecar.Info
            : null;
        var pathStatus = GetPathStatus(canonicalPath, pathHits ?? FindAllAspireOnPath(_logger));
        // Use the wire string from the parsed source so callers see the same
        // identifier the install scripts wrote, not the C# enum name. For
        // sidecars with an unrecognized source value we surface the raw
        // string so users see "(unknown: future-route)" rather than nothing.
        // Route through the shared helper so an empty RawSource collapses to
        // null and the JSON shape of `--self` matches the full discovery walk.
        var route = sidecar is not null ? GetRouteFromSidecar(sidecar) : null;

        return new InstallationInfo
        {
            // Prefer the canonical (resolved + macOS-firmlink-stripped) form
            // for display so the self row's Path column agrees with the form
            // peer rows already use (PATH walks return un-firmlinked entries;
            // candidate sources derive paths from the firmlink-stripped
            // AspireHome). Falling back to the raw process path keeps the row
            // non-empty when resolution fails on a malformed input.
            Path = canonicalPath ?? processPath ?? string.Empty,
            CanonicalPath = canonicalPath,
            Version = VersionHelper.GetDefaultTemplateVersion(),
            Channel = TryReadChannel(),
            Route = route,
            PathStatus = pathStatus,
            Status = InstallationInfoStatus.Ok,
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
        => DiscoverAllAsync(Environment.ProcessPath, cancellationToken);

    /// <summary>
    /// Test seam for <see cref="DiscoverAllAsync(CancellationToken)"/>: lets
    /// tests substitute the running CLI's process path so we can exercise
    /// dedup across firmlinked / un-firmlinked path forms without
    /// manipulating <see cref="Environment.ProcessPath"/> globally.
    /// </summary>
    internal async Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(string? processPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pathHits = FindAllAspireOnPath(_logger).ToList();
        var self = DescribeSelf(processPath, pathHits);
        var aspireHome = _executionContext.AspireHomeDirectory.FullName;
        _logger.LogDebug(
            "Discovery: starting walk. self.Path='{SelfPath}', self.Canonical='{SelfCanonical}', AspireHome='{AspireHome}'.",
            self.Path,
            self.CanonicalPath ?? "(null)",
            aspireHome);

        var results = new List<InstallationInfo> { self };
        // Deduplicate by canonical path (case-insensitive on Windows). The
        // running CLI is always the first row, so peers that resolve to
        // the same canonical path are silently dropped.
        var seen = new HashSet<string>(
            self.CanonicalPath is { Length: > 0 } sp ? [sp] : [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (pathHits.Count == 0)
        {
            _logger.LogDebug("Discovery: no 'aspire' binary found on $PATH.");
        }
        else
        {
            foreach (var pathHit in pathHits)
            {
                _logger.LogDebug(
                    "Discovery: $PATH match: '{Path}' (canonical: '{Canonical}').",
                    pathHit.OriginalPath, pathHit.CanonicalPath);
            }
        }

        var candidateCount = 0;
        var candidateContext = new InstallationCandidateContext(
            s_aspireBinaryName,
            _executionContext.HomeDirectory,
            _executionContext.AspireHomeDirectory,
            pathHits,
            _logger,
            cancellationToken);
        foreach (var candidate in EnumerateDiscoveryCandidates(candidateContext))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateCount++;

            _logger.LogDebug(
                "Discovery: considering candidate #{Index} '{Path}' (origin: {Origin}).",
                candidateCount, candidate.BinaryPath, candidate.Origin);

            // Prefer the candidate's pre-resolved canonical hint when present:
            // $PATH hits already had their canonical resolved by FindAllAspireOnPath,
            // so re-resolving here would (a) double the syscalls and (b) open a
            // TOCTOU window where a symlink swap between the two resolves could
            // produce a different canonical and break dedup. Other sources (release
            // prefix, dogfood, dotnet-tool store) leave the hint null and still
            // resolve here. Empty-string -> skip-candidate semantics are preserved
            // for both paths.
            var canonical = !string.IsNullOrEmpty(candidate.CanonicalPath)
                ? candidate.CanonicalPath
                : CliPathHelper.ResolveSymlinkToFullPath(candidate.BinaryPath, _logger);
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
            var sidecarResult = !string.IsNullOrEmpty(binaryDir)
                ? _sidecarReader.TryRead(binaryDir)
                : new InstallSidecarReadResult.NotFound(string.Empty);
            var pathStatus = GetPathStatus(canonical, pathHits);

            // Only spawn peers that carry readable install metadata. Other PATH hits
            // become notProbed rows so users see they exist, but we never execute them.
            if (sidecarResult is not InstallSidecarReadResult.Ok { Info: var sidecar })
            {
                _logger.LogDebug(
                    "Discovery: candidate '{Canonical}' (origin: {Origin}) did not pass install metadata sidecar read ({SidecarReadResult}) — treating as not-probed.",
                    canonical, candidate.Origin, sidecarResult.GetType().Name);
                results.Add(new InstallationInfo
                {
                    Path = candidate.BinaryPath,
                    CanonicalPath = canonical,
                    PathStatus = pathStatus,
                    Status = InstallationInfoStatus.NotProbed,
                    StatusReason = GetNotProbedReason(sidecarResult),
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
                    // Also derive the channel for PR builds — the channel
                    // is structurally `pr-<N>` for a PR install, and we
                    // can recover it from the install path layout
                    // (dogfood/pr-<N>/bin) or from the informational
                    // version string (<x.y.z>-pr.<N>.<hash>) baked at
                    // build time, so we surface it even when the peer
                    // didn't report it.
                    var route = ok.Info.Route ?? GetRouteFromSidecar(sidecar);
                    var channel = ok.Info.Channel;
                    if (string.IsNullOrEmpty(channel) && sidecar.Source == InstallSource.Pr)
                    {
                        channel = TryDerivePrChannel(canonical);
                    }
                    if (string.IsNullOrEmpty(channel))
                    {
                        // Final attempt: derive the channel from the peer's
                        // reported version. This is the only signal we have
                        // for older peers that don't recognize the
                        // `doctor --self` self-describe contract — they
                        // fall through to the `--version` floor in the
                        // probe and can't report their channel directly,
                        // but the assembly's InformationalVersion has it
                        // baked in for PR builds.
                        channel = TryDerivePrChannelFromVersion(ok.Info.Version);
                    }

                    results.Add(ok.Info with
                    {
                        Path = candidate.BinaryPath,
                        CanonicalPath = canonical,
                        Route = route,
                        Channel = channel,
                        PathStatus = pathStatus,
                    });
                    break;
                case PeerProbeResult.Failed failed:
                    _logger.LogDebug(
                        "Discovery: candidate '{Canonical}' (origin: {Origin}, route: {Route}) failed peer probe: {Reason}.",
                        canonical, candidate.Origin, GetRouteFromSidecar(sidecar), failed.Reason);
                    results.Add(new InstallationInfo
                    {
                        Path = candidate.BinaryPath,
                        CanonicalPath = canonical,
                        Route = GetRouteFromSidecar(sidecar),
                        PathStatus = pathStatus,
                        Status = InstallationInfoStatus.Failed,
                        StatusReason = failed.Reason,
                    });
                    break;
                default:
                    throw new NotSupportedException($"Unsupported peer probe result type '{probe?.GetType().FullName ?? "(null)"}'.");
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
            !string.Equals(Path.GetFileName(dogfoodDir), InstallationDiscoveryLayout.DogfoodDirectoryName, StringComparison.Ordinal))
        {
            return null;
        }

        var label = Path.GetFileName(prDir);
        // Use Ordinal (case-sensitive) to match the dogfood directory check above
        // and the producer side (IdentityChannelReader.IsValidChannel only accepts
        // a lowercase pr-<N> label). Using OrdinalIgnoreCase here would let
        // "Dogfood/Pr-123" fail the dogfood check but pass this one on
        // case-insensitive filesystems, producing an inconsistent classification.
        if (string.IsNullOrEmpty(label) || !label.StartsWith("pr-", StringComparison.Ordinal))
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
    /// Derives the <c>pr-&lt;N&gt;</c> identity channel from the peer's
    /// reported informational version string. PR-channel CI builds bake
    /// versions of the shape <c>&lt;x.y.z&gt;-pr.&lt;N&gt;.&lt;hash&gt;</c>
    /// (for example <c>13.4.0-pr.17115.gcd700928</c>); this method extracts
    /// the digits-only <c>&lt;N&gt;</c> segment and returns
    /// <c>pr-&lt;N&gt;</c>. Stable, staging, daily, and preview versions
    /// don't carry that token and return <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Like <see cref="TryDerivePrChannel(string)"/>, this is a purely
    /// cosmetic enrichment for the user-facing table. It rescues the
    /// channel column for peers that don't recognize the
    /// <c>doctor --self</c> self-describe contract: those fall through to
    /// the <c>--version</c> floor in the probe and can't report their
    /// channel directly, but their assembly's InformationalVersion has
    /// the PR number baked in regardless of route.
    /// </remarks>
    internal static string? TryDerivePrChannelFromVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        var match = PrChannelVersionRegex().Match(version);
        if (!match.Success)
        {
            return null;
        }

        return string.Concat("pr-", match.Groups["number"].Value);
    }

    // Version examples:
    //   13.4.0-pr.17115.gcd700928   -> extract 17115
    //   13.3.0-pr.1234.abc          -> extract 1234
    //   13.4.0-preview.1.99999.1    -> no -pr. -> null
    //   13.4.0                      -> no -pr. -> null
    // Require a leading hyphen so we don't accept a stray "pr." token
    // mid-version (e.g. a hypothetical "13.4.0-fix.pr.1" — defensive,
    // not observed). Require the digits to terminate at '.', '+', or the
    // end of the string so unrelated tokens don't get misclassified as PR
    // channels.
    [GeneratedRegex(@"-pr\.(?<number>[0-9]+)(?:[.+]|$)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex PrChannelVersionRegex();

    private string? TryReadChannel()
    {
        if (_channelReader.TryReadChannel(out var channel, out var error))
        {
            return channel;
        }

        // Same defensive posture as doctor: a misconfigured dev build
        // with no AspireCliChannel assembly metadata must not break
        // aspire doctor.
        _logger.LogDebug("Could not read identity channel for InstallationDiscovery: {Error}", error);
        return null;
    }

    private static string GetNotProbedReason(InstallSidecarReadResult result)
        => result switch
        {
            InstallSidecarReadResult.NotFound => $"No install-route sidecar found at {result.SidecarPath}; peer was not probed.",
            InstallSidecarReadResult.Invalid => $"Install-route sidecar at {result.SidecarPath} could not be read or parsed; peer was not probed.",
            _ => $"No install-route sidecar found at {result.SidecarPath}; peer was not probed.",
        };

    private static string? GetRouteFromSidecar(InstallSidecarInfo sidecar)
        => sidecar.Source.ToWireString() ?? (string.IsNullOrEmpty(sidecar.RawSource) ? null : sidecar.RawSource);

    /// <summary>
    /// Walks <c>$PATH</c> looking for every <c>aspire</c> /
    /// <c>aspire.exe</c> binary the shell could resolve.
    /// </summary>
    private static IEnumerable<InstallationPathHit> FindAllAspireOnPath(ILogger? logger)
    {
        foreach (var candidate in PathLookupHelper.FindAllFullPathsFromPath("aspire"))
        {
            var canonical = CliPathHelper.ResolveSymlinkToFullPath(candidate, logger);
            if (!string.IsNullOrEmpty(canonical))
            {
                yield return new InstallationPathHit(candidate, canonical);
            }
        }
    }

    private IEnumerable<InstallationDiscoveryCandidate> EnumerateDiscoveryCandidates(InstallationCandidateContext context)
    {
        foreach (var source in _candidateSources)
        {
            foreach (var candidate in source.GetCandidates(context))
            {
                yield return candidate;
            }
        }
    }

    private static string GetPathStatus(string? canonicalPath, IEnumerable<InstallationPathHit> pathHits)
    {
        if (string.IsNullOrEmpty(canonicalPath))
        {
            return InstallationPathStatus.NotOnPath;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var isFirst = true;
        foreach (var pathHit in pathHits)
        {
            if (comparer.Equals(pathHit.CanonicalPath, canonicalPath))
            {
                return isFirst ? InstallationPathStatus.Active : InstallationPathStatus.Shadowed;
            }

            isFirst = false;
        }

        return InstallationPathStatus.NotOnPath;
    }

    private static IReadOnlyList<IInstallationCandidateSource> CreateDefaultCandidateSources()
        =>
        [
            new PathInstallationCandidateSource(),
            new ReleasePrefixInstallationCandidateSource(),
            new DogfoodInstallationCandidateSource(),
            new DotnetToolStoreInstallationCandidateSource(),
        ];
}
