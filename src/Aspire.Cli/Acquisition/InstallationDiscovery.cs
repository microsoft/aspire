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
        var canonicalPath = ResolveCanonicalPath(processPath);
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
            IsOnPath = IsOnPathSelf(canonicalPath),
            Status = InstallationInfoStatus.Ok,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var self = DescribeSelf();
        var results = new List<InstallationInfo> { self };
        // Deduplicate by canonical path (case-insensitive on Windows). The
        // running CLI is always the first row, so peers that resolve to
        // the same canonical path are silently dropped.
        var seen = new HashSet<string>(
            self.CanonicalPath is { Length: > 0 } sp ? [sp] : [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var pathHit = FindFirstAspireOnPath();
        foreach (var candidate in EnumerateDiscoveryCandidates(pathHit))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var canonical = ResolveCanonicalPath(candidate.BinaryPath);
            if (string.IsNullOrEmpty(canonical) || !seen.Add(canonical))
            {
                continue;
            }

            var binaryDir = Path.GetDirectoryName(canonical);
            var sidecar = !string.IsNullOrEmpty(binaryDir) ? _sidecarReader.TryRead(binaryDir) : null;

            // Trust gate (RD-2): we only spawn peers that carry a readable
            // install-route sidecar with a known source. Untrusted PATH
            // hits become notProbed rows so users still see they exist
            // but we never execute them.
            if (sidecar is null || sidecar.Source == InstallSource.Unknown)
            {
                results.Add(new InstallationInfo
                {
                    Path = candidate.BinaryPath,
                    CanonicalPath = canonical,
                    Status = InstallationInfoStatus.NotProbed,
                    StatusReason = sidecar is null
                        ? "No install-route sidecar found (trust gate)."
                        : $"Sidecar reports unknown source '{sidecar.RawSource ?? "(empty)"}' (trust gate).",
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
                    results.Add(ok.Info with
                    {
                        Path = candidate.BinaryPath,
                        CanonicalPath = canonical,
                        Route = ok.Info.Route ?? sidecar.Source.ToWireString(),
                        IsOnPath = canonical.Equals(pathHit?.CanonicalPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                    });
                    break;
                case PeerProbeResult.Failed failed:
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

        return results;
    }

    /// <summary>
    /// Resolves any symlinks in <paramref name="processPath"/> so that two
    /// PATH entries pointing at the same backing file produce the same
    /// canonical identifier. Mirrors the symlink resolution that
    /// <see cref="Bundles.BundleService"/> uses for sidecar lookup so
    /// <c>info</c> and <c>BundleService</c> agree on identity.
    /// </summary>
    private static string? ResolveCanonicalPath(string? processPath)
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
        catch (IOException)
        {
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
    private static bool IsOnPathSelf(string? canonicalSelfPath)
    {
        if (string.IsNullOrEmpty(canonicalSelfPath))
        {
            return false;
        }

        var first = FindFirstAspireOnPath();
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
    private static PathHit? FindFirstAspireOnPath()
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
                var canonical = ResolveCanonicalPath(candidate);
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
    /// </summary>
    private static IEnumerable<DiscoveryCandidate> EnumerateDiscoveryCandidates(PathHit? pathHit)
    {
        if (pathHit is not null)
        {
            yield return new DiscoveryCandidate(pathHit.OriginalPath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        // Release-script default.
        var releaseBinary = Path.Combine(home, ".aspire", "bin", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        if (File.Exists(releaseBinary))
        {
            yield return new DiscoveryCandidate(releaseBinary);
        }

        // PR-script default: ~/.aspire/dogfood/pr-*/bin/aspire[.exe].
        var dogfoodRoot = Path.Combine(home, ".aspire", "dogfood");
        if (Directory.Exists(dogfoodRoot))
        {
            foreach (var prDir in EnumerateDirectoriesSafe(dogfoodRoot))
            {
                var binary = Path.Combine(prDir, "bin", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
                if (File.Exists(binary))
                {
                    yield return new DiscoveryCandidate(binary);
                }
            }
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
                matches = [];
                _ = ex;
            }
            foreach (var match in matches)
            {
                yield return new DiscoveryCandidate(match);
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return [];
        }
    }

    private sealed record PathHit(string OriginalPath, string CanonicalPath);

    private sealed record DiscoveryCandidate(string BinaryPath);
}

