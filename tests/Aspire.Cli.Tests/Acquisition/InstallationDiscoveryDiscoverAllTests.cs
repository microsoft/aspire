// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="InstallationDiscovery.DiscoverAllAsync(System.Threading.CancellationToken)"/>
/// focused on install metadata requirements and dedup-by-canonical-path semantics.
/// PATH and well-known-prefix walks are exercised via an isolated
/// HOME-equivalent so a developer's real home directory doesn't leak
/// into the test.
/// </summary>
/// <remarks>
/// Tests in this class mutate process-wide environment variables (PATH,
/// PATHEXT, HOME/USERPROFILE) via <see cref="EnvVarOverride"/>. xUnit runs
/// test classes in parallel by default, so any other test in this assembly
/// that reads PATH (directly or via <c>Process.Start</c> / path lookup
/// helpers) could see the override transiently. The collection definition
/// disables parallelization across these tests and any other suite that
/// joins <c>EnvVarMutatingTests</c>.
/// </remarks>
[Collection(EnvVarMutatingTestCollection.Name)]
public class InstallationDiscoveryDiscoverAllTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DiscoverAllAsync_PathHit_WithoutSidecar_IsListedAsNotProbed_AndNeverSpawned()
    {
        // A binary on $PATH with no .aspire-install.json next to it must not be
        // spawned. The user-installed binary on PATH is the most dangerous case:
        // if a user runs `aspire doctor`, we cannot execute arbitrary
        // same-named binaries we happened to find.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "no-sidecar-bin");
        Directory.CreateDirectory(pathDir);
        var noSidecarBinary = WriteFakeBinary(pathDir);

        var probe = new FakePeerInstallProbe();
        using var _ = new EnvVarOverride("PATH", pathDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        // Install metadata is required before probing: this PATH hit must not be probed.
        Assert.DoesNotContain(probe.ProbedPaths, p => string.Equals(p, noSidecarBinary, StringComparison.Ordinal));

        var noSidecarRow = Assert.Single(results, r =>
            string.Equals(r.CanonicalPath, noSidecarBinary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.NotProbed, noSidecarRow.Status);
        var expectedSidecarPath = Path.Combine(pathDir, InstallSidecarReader.SidecarFileName);
        Assert.Equal($"No install-route sidecar found at {expectedSidecarPath}; peer was not probed.", noSidecarRow.StatusReason);
    }

    [Fact]
    public async Task DiscoverAllAsync_PathHit_WithUnreadableSidecar_IsListedAsNotProbedWithInvalidSidecarReason()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are required to create a deterministic unreadable sidecar.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "unreadable-bin");
        Directory.CreateDirectory(pathDir);
        var binary = WriteFakeBinary(pathDir);
        var sidecarPath = Path.Combine(pathDir, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(sidecarPath, "{\"source\":\"script\"}");
        var originalMode = File.GetUnixFileMode(sidecarPath);

        try
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.None);

            var probe = new FakePeerInstallProbe();
            using var _ = new EnvVarOverride("PATH", pathDir);

            var discovery = NewDiscovery(probe, workspace);
            var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(probe.ProbedPaths, p => string.Equals(p, binary, StringComparison.Ordinal));
            var row = Assert.Single(results, r => string.Equals(r.CanonicalPath, binary, StringComparison.Ordinal));
            Assert.Equal(InstallationInfoStatus.NotProbed, row.Status);
            Assert.Equal($"Install-route sidecar at {sidecarPath} could not be read or parsed; peer was not probed.", row.StatusReason);
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task DiscoverAllAsync_PathHit_WithMalformedSidecar_IsListedAsNotProbedWithInvalidSidecarReason()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "malformed-bin");
        Directory.CreateDirectory(pathDir);
        var binary = WriteFakeBinary(pathDir);
        var sidecarPath = Path.Combine(pathDir, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(sidecarPath, "{not valid json");

        var probe = new FakePeerInstallProbe();
        using var _ = new EnvVarOverride("PATH", pathDir);

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var row = Assert.Single(results, r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.NotProbed, row.Status);
        Assert.Equal($"Install-route sidecar at {sidecarPath} could not be read or parsed; peer was not probed.", row.StatusReason);
    }

    [Fact]
    public async Task DiscoverAllAsync_TrustedSidecar_IsSpawnedAndDecoratedWithDiscoveredPath()
    {
        // A binary with a script-route sidecar in its directory has enough
        // install metadata to be probed. The peer probe is called, and its returned
        // InstallationInfo is merged with the discovered path so the row
        // displayed to the user matches what `which` would show.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteFakeBinary(binDir);
        File.WriteAllText(Path.Combine(binDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"script\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = "/peer-says/aspire",
                Version = "12.5.0",
                Channel = "stable",
                Route = "script",
                Status = InstallationInfoStatus.Ok,
            }),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        var discoveredRow = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal("12.5.0", discoveredRow.Version);
        Assert.Equal("stable", discoveredRow.Channel);
        // Discovered path wins over what the peer reported, so the table
        // reflects where the binary lives on disk.
        Assert.Equal(binary, discoveredRow.Path);
    }

    [Fact]
    public async Task DiscoverAllAsync_PathStatusTracksActiveShadowedAndOffPathInstalls()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstPathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "path-first");
        var secondPathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "path-second");
        var offPathDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(firstPathDir);
        Directory.CreateDirectory(secondPathDir);
        Directory.CreateDirectory(offPathDir);

        var firstPathBinary = WriteTrustedFakeBinary(firstPathDir, "script");
        var secondPathBinary = WriteTrustedFakeBinary(secondPathDir, "pr");
        var offPathBinary = WriteTrustedFakeBinary(offPathDir, "script");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [firstPathBinary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = firstPathBinary,
                Version = "13.0.1",
                Status = InstallationInfoStatus.Ok,
            }),
            [secondPathBinary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = secondPathBinary,
                Version = "13.0.2",
                Status = InstallationInfoStatus.Ok,
            }),
            [offPathBinary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = offPathBinary,
                Version = "13.0.3",
                Status = InstallationInfoStatus.Ok,
            }),
        });

        using var pathOverride = new EnvVarOverride("PATH", firstPathDir + Path.PathSeparator + secondPathDir);
        using var pathExtOverride = new EnvVarOverride("PATHEXT", ".EXE");

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var firstPathRow = results.Single(r =>
            string.Equals(r.CanonicalPath, firstPathBinary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var secondPathRow = results.Single(r =>
            string.Equals(r.CanonicalPath, secondPathBinary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var offPathRow = results.Single(r =>
            string.Equals(r.CanonicalPath, offPathBinary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        Assert.Equal(InstallationPathStatus.Active, firstPathRow.PathStatus);
        Assert.Equal(InstallationPathStatus.Shadowed, secondPathRow.PathStatus);
        Assert.Equal(InstallationPathStatus.NotOnPath, offPathRow.PathStatus);
    }

    [Fact]
    public async Task DiscoverAllAsync_UnknownSidecarSource_WithSuccessfulProbe_ProbesAndSurfacesRawRoute()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteTrustedFakeBinary(binDir, "apt");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.1.0",
                Channel = "daily",
                Status = InstallationInfoStatus.Ok,
            }),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var row = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.Ok, row.Status);
        Assert.Equal("apt", row.Route);
        Assert.Equal("13.1.0", row.Version);
        Assert.Equal("daily", row.Channel);
    }

    [Fact]
    public async Task DiscoverAllAsync_UnknownSidecarSource_WithFailedProbe_ProbesAndSurfacesFailedWithRawRoute()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteTrustedFakeBinary(binDir, "apt");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Failed("simulated probe failure"),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var row = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.Failed, row.Status);
        Assert.Equal("apt", row.Route);
        Assert.Equal("simulated probe failure", row.StatusReason);
    }

    [Fact]
    public async Task DiscoverAllAsync_PeerProbeFails_RowSurvivesAsFailed_WithRouteIntact()
    {
        // A peer that fails (timeout / non-zero exit / invalid JSON) is per-row,
        // not whole-command. The route from the sidecar is still surfaced so the
        // user sees "this is a PR install but it wouldn't talk to me", not nothing.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-9999", "bin");
        Directory.CreateDirectory(prDir);
        var binary = WriteFakeBinary(prDir);
        File.WriteAllText(Path.Combine(prDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"pr\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Failed("simulated peer hang"),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var row = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.Failed, row.Status);
        Assert.Equal("pr", row.Route);
        Assert.Contains("simulated peer hang", row.StatusReason!);
    }

    [Fact]
    public async Task DiscoverAllAsync_UnknownPeerProbeResult_Throws()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteFakeBinary(binDir);
        File.WriteAllText(Path.Combine(binDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"script\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new UnknownPeerProbeResult(),
        });

        var discovery = NewDiscovery(probe, workspace);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => discovery.DiscoverAllAsync(TestContext.Current.CancellationToken));
        Assert.Contains(nameof(UnknownPeerProbeResult), exception.Message);
    }

    [Theory]
    [InlineData(true, InstallationInfoStatus.Failed)]
    [InlineData(false, InstallationInfoStatus.NotProbed)]
    public async Task DiscoverAllAsync_ProbeFailureAndMissingInstallMetadata_SurfaceDistinctStatuses(bool hasInstallMetadata, string expectedStatus)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, hasInstallMetadata ? "metadata-bin" : "no-metadata-bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteFakeBinary(binDir);

        if (hasInstallMetadata)
        {
            File.WriteAllText(Path.Combine(binDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"script\"}");
        }

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Failed("peer returned malformed JSON"),
        });

        using var _ = new EnvVarOverride("PATH", binDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var row = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(expectedStatus, row.Status);

        if (hasInstallMetadata)
        {
            Assert.Contains(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Equal("script", row.Route);
            Assert.Contains("peer returned malformed JSON", row.StatusReason!);
        }
        else
        {
            Assert.DoesNotContain(probe.ProbedPaths, p => string.Equals(p, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Contains("peer was not probed", row.StatusReason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DiscoverAllAsync_PrRoute_DerivesChannelFromDogfoodPathWhenPeerOmits()
    {
        // The structural channel for a PR install is `pr-<N>` regardless
        // of whether the older peer's --version output includes channel
        // info. Discovery should overlay it from the dogfood/pr-<N>/
        // path layout when probe.Channel comes back null.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-12345", "bin");
        Directory.CreateDirectory(prDir);
        var binary = WriteFakeBinary(prDir);
        File.WriteAllText(Path.Combine(prDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"pr\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            // Older peer using --version fallback: version only, no channel.
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0-pr.12345.gabcdef",
                Status = InstallationInfoStatus.Ok,
            }),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var prRow = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal("pr-12345", prRow.Channel);
        Assert.Equal("pr", prRow.Route);
        Assert.Equal("13.4.0-pr.12345.gabcdef", prRow.Version);
    }

    [Fact]
    public async Task DiscoverAllAsync_PrRoute_DoesNotOverwritePeerReportedChannel()
    {
        // When the peer DOES report a channel, the discovery layer must not overwrite it
        // with the path-derived value, even if they happen to match.
        // This guards against a bug where overlay logic assumes channel
        // is always missing on the fallback path.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-12345", "bin");
        Directory.CreateDirectory(prDir);
        var binary = WriteFakeBinary(prDir);
        File.WriteAllText(Path.Combine(prDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"pr\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0-pr.12345.gabcdef",
                Channel = "pr-12345-from-peer", // intentionally distinct
                Status = InstallationInfoStatus.Ok,
            }),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var prRow = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal("pr-12345-from-peer", prRow.Channel);
    }

    [Fact]
    public async Task DiscoverAllAsync_BrewRoute_DerivesPrChannelFromVersionWhenPeerOmits()
    {
        // Brew-installed PR builds (e.g. from `brew install aspire@pr-N`) live
        // under a path that doesn't carry the `dogfood/pr-<N>/bin` shape, so
        // the path-based derivation can't help. The version string, on the
        // other hand, IS baked at build time as `<x.y.z>-pr.<N>.<hash>` —
        // discovery should fall back to that signal so older brew peers,
        // which don't recognize the `doctor --self` self-describe contract,
        // still surface their channel instead of "(unknown)".
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var brewDir = Path.Combine(workspace.WorkspaceRoot.FullName, "Caskroom", "aspire", "13.4.0-pr.17115.gcd700928");
        Directory.CreateDirectory(brewDir);
        var binary = WriteFakeBinary(brewDir);
        File.WriteAllText(Path.Combine(brewDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"brew\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            // Older brew peer: doctor --self unsupported, so the probe
            // took the --version fallback path and reported version only.
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0-pr.17115.gcd700928",
                Status = InstallationInfoStatus.Ok,
            }),
        });
        using var _ = new EnvVarOverride("PATH", brewDir);

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var brewRow = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal("pr-17115", brewRow.Channel);
        Assert.Equal("brew", brewRow.Route);
        Assert.Equal("13.4.0-pr.17115.gcd700928", brewRow.Version);
    }

    [Fact]
    public async Task DiscoverAllAsync_BrewRoute_LeavesChannelNullForStableVersion()
    {
        // Same shape as the previous test, but with a non-PR version. The
        // version-based derivation must return null (we can't recover the
        // channel from a `13.4.0` style stable version), so the channel
        // stays unset and the doctor table renders "(unknown)".
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var brewDir = Path.Combine(workspace.WorkspaceRoot.FullName, "Caskroom", "aspire", "13.4.0");
        Directory.CreateDirectory(brewDir);
        var binary = WriteFakeBinary(brewDir);
        File.WriteAllText(Path.Combine(brewDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"brew\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0",
                Status = InstallationInfoStatus.Ok,
            }),
        });
        using var _ = new EnvVarOverride("PATH", brewDir);

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var brewRow = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Null(brewRow.Channel);
        Assert.Equal("brew", brewRow.Route);
    }

    [Theory]
    [InlineData("pr-")]              // empty PR number suffix
    [InlineData("pr-not-digits")]    // non-digit suffix
    [InlineData("pull-12345")]       // wrong prefix
    [InlineData("PR-1234")]          // wrong case — producer only emits lowercase
    [InlineData("Pr-1234")]          // wrong case — producer only emits lowercase
    public void TryDerivePrChannel_RejectsMalformedPrLabels(string labelName)
    {
        // We only synthesize a channel when the directory name strictly
        // matches `pr-<digits>`; anything else (custom --install-path
        // installs, manual layouts, future label shapes) returns null so
        // we don't surface a misleading channel string.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binary = Path.Combine(workspace.WorkspaceRoot.FullName, "dogfood", labelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);

        var derived = InstallationDiscovery.TryDerivePrChannel(binary);
        Assert.Null(derived);
    }

    [Fact]
    public void TryDerivePrChannel_RejectsNonDogfoodGrandparent()
    {
        // The grandparent dir must literally be `dogfood` — anything else
        // (e.g., `~/.aspire/staging/pr-1/bin`) is not the conventional
        // PR-script layout and we shouldn't synthesize a channel from it.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binary = Path.Combine(workspace.WorkspaceRoot.FullName, "staging", "pr-1234", "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);

        var derived = InstallationDiscovery.TryDerivePrChannel(binary);
        Assert.Null(derived);
    }

    [Fact]
    public void TryDerivePrChannel_AcceptsValidDogfoodLayout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binary = Path.Combine(workspace.WorkspaceRoot.FullName, "dogfood", "pr-9876", "bin", "aspire");

        var derived = InstallationDiscovery.TryDerivePrChannel(binary);
        Assert.Equal("pr-9876", derived);
    }

    [Theory]
    [InlineData("13.4.0-pr.17115.gcd700928", "pr-17115")]    // canonical PR build
    [InlineData("13.3.0-pr.1234.abc",         "pr-1234")]     // short hash suffix
    [InlineData("13.4.0-pr.5",                "pr-5")]        // missing hash suffix (defensive)
    [InlineData("13.4.0-pr.99999+build.42",   "pr-99999")]    // SemVer build-metadata separator
    public void TryDerivePrChannelFromVersion_AcceptsPrShapedVersions(string version, string expected)
    {
        Assert.Equal(expected, InstallationDiscovery.TryDerivePrChannelFromVersion(version));
    }

    [Theory]
    [InlineData(null)]                                  // null version (peer reported nothing)
    [InlineData("")]                                    // empty version
    [InlineData("13.4.0")]                              // stable release
    [InlineData("13.4.0-staging.42")]                   // staging release
    [InlineData("13.4.0-daily.abc123")]                 // daily release
    [InlineData("13.4.0-preview.1.99999.1")]            // preview build (predates pr-channel)
    [InlineData("13.4.0-pr.")]                          // marker with no digits
    [InlineData("13.4.0-pr.foo.bar")]                   // non-digit suffix
    [InlineData("13.4.0-pr.1foo.bar")]                  // mixed digits/letters in the N segment
    [InlineData("13.4.0-prerelease.1.gabc")]            // contains "pr" but not "-pr."
    public void TryDerivePrChannelFromVersion_RejectsNonPrShapedVersions(string? version)
    {
        Assert.Null(InstallationDiscovery.TryDerivePrChannelFromVersion(version));
    }

    [Fact]
    public async Task DiscoverAllAsync_LogsInstallMetadataRejection_AtDebugLevel()
    {
        // When the install metadata check rejects a candidate (no sidecar), the user
        // should see why in --log-level debug output. Without this, an
        // install that "doesn't show up correctly" in `aspire doctor`
        // is hard to diagnose.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "no-sidecar-bin");
        Directory.CreateDirectory(pathDir);
        WriteFakeBinary(pathDir);

        using var _ = new EnvVarOverride("PATH", pathDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

        var capturedLog = new CapturingLogger<InstallationDiscovery>();
        var discovery = new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader("local"),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: new FakePeerInstallProbe(),
            executionContext: CreateExecutionContext(workspace),
            logger: capturedLog);

        await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(capturedLog.Entries, e =>
            e.Level == LogLevel.Debug &&
            e.Message.Contains("did not pass install metadata sidecar read", StringComparison.Ordinal) &&
            e.Message.Contains("NotFound", StringComparison.Ordinal) &&
            e.Message.Contains("not-probed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAllAsync_LogsDogfoodDirectoryWithoutBinary_AtDebugLevel()
    {
        // A stale ~/.aspire/dogfood/pr-N directory without a bin/aspire
        // inside (failed install, partial uninstall, manual mucking) is
        // worth flagging in debug output so the user can correlate.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var staleDogfoodDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-9999");
        Directory.CreateDirectory(staleDogfoodDir); // exists, but no bin/aspire inside

        var capturedLog = new CapturingLogger<InstallationDiscovery>();
        var discovery = new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader("local"),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: new FakePeerInstallProbe(),
            executionContext: CreateExecutionContext(workspace),
            logger: capturedLog);

        await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(capturedLog.Entries, e =>
            e.Level == LogLevel.Debug &&
            e.Message.Contains(staleDogfoodDir, StringComparison.Ordinal) &&
            e.Message.Contains("not classifying as a real install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAllAsync_UnreadableDogfoodRoot_DoesNotFailDiscovery()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are required to create a deterministic unreadable directory.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dogfoodRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood");
        Directory.CreateDirectory(dogfoodRoot);
        var originalMode = File.GetUnixFileMode(dogfoodRoot);

        try
        {
            File.SetUnixFileMode(dogfoodRoot, UnixFileMode.None);

            var capturedLog = new CapturingLogger<InstallationDiscovery>();
            var discovery = new InstallationDiscovery(
                channelReader: new FakeIdentityChannelReader("local"),
                sidecarReader: new InstallSidecarReader(),
                peerProbe: new FakePeerInstallProbe(),
                executionContext: CreateExecutionContext(workspace),
                logger: capturedLog);

            var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

            Assert.NotEmpty(results);
            Assert.Equal(InstallationInfoStatus.Ok, results[0].Status);
            Assert.Contains(capturedLog.Entries, e =>
                e.Level == LogLevel.Debug &&
                e.Message.Contains("failed to enumerate directories", StringComparison.Ordinal) &&
                e.Message.Contains(dogfoodRoot, StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(dogfoodRoot, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DiscoverAllAsync_UnreadableDotnetToolStore_DoesNotFailDiscovery()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are required to create a deterministic unreadable directory.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolStore = Path.Combine(workspace.WorkspaceRoot.FullName, ".dotnet", "tools", ".store", "aspire.cli");
        Directory.CreateDirectory(toolStore);
        var originalMode = File.GetUnixFileMode(toolStore);

        try
        {
            File.SetUnixFileMode(toolStore, UnixFileMode.None);

            var capturedLog = new CapturingLogger<InstallationDiscovery>();
            var discovery = new InstallationDiscovery(
                channelReader: new FakeIdentityChannelReader("local"),
                sidecarReader: new InstallSidecarReader(),
                peerProbe: new FakePeerInstallProbe(),
                executionContext: CreateExecutionContext(workspace),
                logger: capturedLog);

            var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

            // The contract is "an unreadable tool store does not break discovery":
            // self still resolves, and no entry from the unreadable tree leaks
            // into the result set. The candidate source uses
            // EnumerationOptions.IgnoreInaccessible to silently skip the
            // inaccessible root, so MoveNext returns false and the walk
            // produces zero candidates — there is intentionally no error log
            // to assert on for this case (an unreadable subtree is exactly
            // the IgnoreInaccessible scenario, not an error).
            Assert.NotEmpty(results);
            Assert.Equal(InstallationInfoStatus.Ok, results[0].Status);
            Assert.DoesNotContain(results, info =>
                info.Path.StartsWith(toolStore, StringComparison.Ordinal) ||
                (info.CanonicalPath?.StartsWith(toolStore, StringComparison.Ordinal) ?? false));
        }
        finally
        {
            File.SetUnixFileMode(toolStore, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DiscoverAllAsync_RunningCliIsAlwaysFirst()
    {
        // Self must appear first regardless of what walks find — both for
        // the table display contract ("(current)" marker) and to keep peer
        // dedup deterministic.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var discovery = NewDiscovery(new FakePeerInstallProbe(), workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Equal(InstallationInfoStatus.Ok, results[0].Status);
        var canonicalSelf = ResolveCanonicalProcessPath();
        Assert.Equal(canonicalSelf, results[0].CanonicalPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAllAsync_RunningCliUsesIdentityChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var discovery = NewDiscovery(new FakePeerInstallProbe(), workspace, identityChannel: "staging");
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Equal("staging", results[0].Channel);
    }

    [Fact]
    public async Task DiscoverAllAsync_HonorsExecutionContextHomeDirectory_WithoutEnvOverride()
    {
        // Regression: discovery must read home from the injected
        // CliExecutionContext, not from Environment.GetFolderPath /
        // USERPROFILE. The HOME/USERPROFILE env-var override technique is
        // platform-conditional — it propagates through GetFolderPath on
        // Unix but not on Windows (where GetFolderPath reads from the
        // security token). Injecting via CliExecutionContext.HomeDirectory
        // is the only redirection that works uniformly. This test
        // intentionally does NOT set HOME/USERPROFILE so it would fail on
        // Windows pre-fix.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var releaseBinDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bin");
        Directory.CreateDirectory(releaseBinDir);
        var binary = WriteFakeBinary(releaseBinDir);
        File.WriteAllText(Path.Combine(releaseBinDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"script\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0",
                Channel = "stable",
                Route = "script",
                Status = InstallationInfoStatus.Ok,
            }),
        });

        var discovery = NewDiscovery(probe, workspace);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(results, r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAllAsync_UsesAspireHomeDirectoryForPortableInstallPrefixes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var userHome = workspace.CreateDirectory("user-home");
        var aspireHome = workspace.CreateDirectory("portable-home");
        var releaseBinDir = Path.Combine(aspireHome.FullName, "bin");
        Directory.CreateDirectory(releaseBinDir);
        var binary = WriteFakeBinary(releaseBinDir);
        File.WriteAllText(Path.Combine(releaseBinDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"localhive\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                Version = "13.4.0",
                Channel = "local",
                Route = "localhive",
                Status = InstallationInfoStatus.Ok,
            }),
        });
        var root = workspace.WorkspaceRoot;
        var context = new CliExecutionContext(
            workingDirectory: root,
            hivesDirectory: root,
            cacheDirectory: root,
            sdksDirectory: root,
            logsDirectory: root,
            logFilePath: Path.Combine(root.FullName, "test.log"),
            homeDirectory: userHome,
            aspireHomeDirectory: aspireHome);
        var discovery = new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader("local"),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: probe,
            executionContext: context,
            logger: NullLogger<InstallationDiscovery>.Instance);

        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(results, r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAllAsync_OnMacOS_DedupsSelfAgainstPathHit_AcrossFirmlinkBoundary()
    {
        // Bug A regression: when Environment.ProcessPath comes back firmlinked
        // (/private/var/...) but the same physical binary is discovered via $PATH
        // in the un-firmlinked form (/var/...), the dedup `seen` HashSet treats
        // them as distinct strings and the same install lands in the table twice.
        // The fix lives in CliPathHelper.ResolveSymlinkToFullPath which strips
        // macOS firmlink prefixes; this test pins the end-to-end behavior so
        // a regression in either the helper or the dedup site is caught.
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Firmlink dedup only applies on macOS where /var → /private/var is a firmlink.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, "firmlink-self-bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteTrustedFakeBinary(binDir, "pr");

        // Drop the binary's bin directory at the front of PATH so the PATH walk
        // discovers it. PATH entries don't pass through firmlinking, so this
        // appears to discovery as /var/folders/.../aspire.
        using var pathOverride = new EnvVarOverride(
            "PATH",
            binDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

        // Simulate the macOS .NET runtime: Environment.ProcessPath always comes
        // back realpath-resolved on macOS, which collapses /var → /private/var.
        // Both forms resolve to the same physical file via APFS firmlinks.
        var firmlinkedProcessPath = "/private" + binary;
        Assert.True(File.Exists(firmlinkedProcessPath), "firmlinked /private/var path should resolve to the same file");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            // The probe is keyed on the canonical (firmlink-stripped) path.
            // If dedup is broken, the second row will be probed too and the
            // result count will exceed 1.
            [binary] = new PeerProbeResult.Ok(new InstallationInfo
            {
                Path = binary,
                CanonicalPath = binary,
                Version = "test-version",
                Channel = "pr-99999",
                Route = "pr",
                Status = InstallationInfoStatus.Ok,
            }),
        });
        var discovery = NewDiscovery(probe, workspace);

        var results = await discovery.DiscoverAllAsync(firmlinkedProcessPath, TestContext.Current.CancellationToken);

        // Exactly one row for this physical binary across every canonical
        // form that could point at it. Pre-fix the self row would carry the
        // firmlinked canonical (`/private/var/...`) while the $PATH-walked
        // candidate would carry the un-firmlinked form (`/var/...`), and
        // both would land in `results` because the dedup `seen` HashSet
        // saw two distinct strings. Match by either form so a regression
        // shows up as a count of two.
        var matching = results
            .Where(r =>
                string.Equals(r.CanonicalPath, binary, StringComparison.Ordinal) ||
                string.Equals(r.CanonicalPath, firmlinkedProcessPath, StringComparison.Ordinal))
            .ToList();
        Assert.Single(matching);
        var row = matching[0];
        // Self surfaces the un-firmlinked path everywhere (Path,
        // CanonicalPath) so the displayed table is consistent with peer
        // rows (PATH walks return un-firmlinked entries; candidate sources
        // derive from the firmlink-stripped AspireHome).
        Assert.Equal(binary, row.Path);
        Assert.Equal(binary, row.CanonicalPath);
        Assert.DoesNotContain("/private/", row.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/", row.CanonicalPath!, StringComparison.Ordinal);
        // Self is `active` on PATH (first hit), not `notOnPath`. Pre-fix, the
        // pathStatus comparison used distinct strings and would land on
        // notOnPath even though the binary was clearly on PATH.
        Assert.Equal(InstallationPathStatus.Active, row.PathStatus);
    }

    [Fact]
    public async Task DiscoverAllAsync_UsesPreResolvedCanonicalHint_WithoutReResolving()
    {
        // Pins the contract that DiscoverAllAsync uses InstallationDiscoveryCandidate.CanonicalPath
        // verbatim instead of re-resolving via CliPathHelper. We construct a candidate whose
        // CanonicalPath is a deliberately-different non-existent path (no real symlink involved):
        // if DiscoverAllAsync were re-resolving, ResolveSymlinkToFullPath would return the real
        // BinaryPath (or an empty string for a fake path) and the resulting row's CanonicalPath
        // would not match the hint. This is the strongest way to pin "we used the hint" because
        // there's no filesystem trick that could produce this canonical from the binary on disk.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binDir = Path.Combine(workspace.WorkspaceRoot.FullName, "hint-bin");
        Directory.CreateDirectory(binDir);
        var binary = WriteFakeBinary(binDir);
        var fakeCanonical = Path.Combine(workspace.WorkspaceRoot.FullName, "deliberately-different-canonical-path", "aspire");

        var probe = new FakePeerInstallProbe();
        var hintSource = new FixedCandidateSource(new InstallationDiscoveryCandidate(binary, "test-hint", fakeCanonical));
        var discovery = new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader("local"),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: probe,
            executionContext: CreateExecutionContext(workspace),
            logger: NullLogger<InstallationDiscovery>.Instance,
            candidateSources: [hintSource]);

        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        // The candidate row carries CanonicalPath = fakeCanonical, proving DiscoverAllAsync
        // did NOT re-resolve via ResolveSymlinkToFullPath (which would have returned `binary`
        // or empty). The Path field carries the original BinaryPath unchanged.
        var hintRow = Assert.Single(results, r => string.Equals(r.Path, binary, StringComparison.Ordinal));
        Assert.Equal(fakeCanonical, hintRow.CanonicalPath);
    }

    private sealed class FixedCandidateSource(params InstallationDiscoveryCandidate[] candidates) : IInstallationCandidateSource
    {
        public IEnumerable<InstallationDiscoveryCandidate> GetCandidates(InstallationCandidateContext context) => candidates;
    }

    private static string ResolveCanonicalProcessPath()
    {
        var path = Environment.ProcessPath!;
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static InstallationDiscovery NewDiscovery(FakePeerInstallProbe probe, TemporaryWorkspace workspace, string identityChannel = "local")
    {
        return new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader(identityChannel),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: probe,
            executionContext: CreateExecutionContext(workspace),
            logger: NullLogger<InstallationDiscovery>.Instance);
    }

    /// <summary>
    /// Builds a <see cref="CliExecutionContext"/> whose <c>HomeDirectory</c>
    /// points at the test workspace. This is the canonical knob the
    /// <see cref="InstallationDiscovery"/> walk reads to resolve
    /// <c>~/.aspire</c> and <c>~/.dotnet</c> — bypassing the developer's real
    /// home directory and avoiding Windows-specific behavior of
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// (which ignores <c>USERPROFILE</c> overrides on Windows).
    /// </summary>
    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace)
    {
        var root = workspace.WorkspaceRoot;
        return new CliExecutionContext(
            workingDirectory: root,
            hivesDirectory: root,
            cacheDirectory: root,
            sdksDirectory: root,
            logsDirectory: root,
            logFilePath: Path.Combine(root.FullName, "test.log"),
            homeDirectory: root);
    }

    /// <summary>
    /// Writes a stub "binary" file to disk. The discovery walk only checks
    /// existence; it never executes — the FakePeerInstallProbe handles
    /// what would have been the spawn.
    /// </summary>
    private static string WriteFakeBinary(string dir)
    {
        var name = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0x00]); // existence is what matters
        if (!OperatingSystem.IsWindows())
        {
            // Match shell semantics: a non-executable file is not a binary on PATH.
            // PathLookupHelper.FindFullPathFromPath honors the executable bit on Unix,
            // so without this chmod the PATH walk would skip the test fixture.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private static string WriteTrustedFakeBinary(string dir, string source)
    {
        var binary = WriteFakeBinary(dir);
        File.WriteAllText(Path.Combine(dir, InstallSidecarReader.SidecarFileName), $"{{\"source\":\"{source}\"}}");
        return binary;
    }
}

/// <summary>
/// Collection definition that disables parallel execution for tests which
/// mutate process-wide environment variables (PATH, PATHEXT, HOME) and
/// would otherwise race with other tests in the assembly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarMutatingTestCollection
{
    public const string Name = "EnvVarMutatingTests";
}
