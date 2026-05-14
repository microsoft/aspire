// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="InstallationDiscovery.DiscoverAllAsync"/>
/// focused on the trust gate (RD-2) and dedup-by-canonical-path semantics.
/// PATH and well-known-prefix walks are exercised via an isolated
/// HOME-equivalent so a developer's real home directory doesn't leak
/// into the test.
/// </summary>
public class InstallationDiscoveryDiscoverAllTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DiscoverAllAsync_PathHit_WithoutSidecar_IsListedAsNotProbed_AndNeverSpawned()
    {
        // RD-2 trust gate: a binary on $PATH with no .aspire-install.json
        // next to it must NOT be spawned. The user-installed binary on
        // PATH is the most dangerous case: if a user runs `aspire info --all`
        // we cannot execute arbitrary same-named binaries we happened to
        // find.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pathDir = Path.Combine(workspace.WorkspaceRoot.FullName, "untrusted-bin");
        Directory.CreateDirectory(pathDir);
        var untrustedBinary = WriteFakeBinary(pathDir);

        var probe = new FakePeerInstallProbe();
        using var _ = new EnvVarOverride("PATH", pathDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
        using var __ = new EnvVarOverride("HOME", workspace.WorkspaceRoot.FullName);
        using var ___ = new EnvVarOverride("USERPROFILE", workspace.WorkspaceRoot.FullName);

        var discovery = NewDiscovery(probe);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        // Trust gate enforces: untrusted PATH hit must NOT be probed.
        Assert.DoesNotContain(probe.ProbedPaths, p => string.Equals(p, untrustedBinary, StringComparison.Ordinal));

        var untrustedRow = Assert.Single(results, r =>
            string.Equals(r.CanonicalPath, untrustedBinary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.NotProbed, untrustedRow.Status);
        Assert.Contains("trust gate", untrustedRow.StatusReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAllAsync_TrustedSidecar_IsSpawnedAndDecoratedWithDiscoveredPath()
    {
        // A binary with a script-route sidecar in its directory passes the
        // trust gate. The peer probe is called, and its returned
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

        using var _ = new EnvVarOverride("HOME", workspace.WorkspaceRoot.FullName);
        using var __ = new EnvVarOverride("USERPROFILE", workspace.WorkspaceRoot.FullName);

        var discovery = NewDiscovery(probe);
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
    public async Task DiscoverAllAsync_PeerProbeFails_RowSurvivesAsNotProbed_WithRouteIntact()
    {
        // RD-10: a peer that fails (timeout / non-zero exit / invalid JSON)
        // is per-row, not whole-command. The route from the sidecar is
        // still surfaced so the user sees "this is a PR install but it
        // wouldn't talk to me", not nothing.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-9999", "bin");
        Directory.CreateDirectory(prDir);
        var binary = WriteFakeBinary(prDir);
        File.WriteAllText(Path.Combine(prDir, InstallSidecarReader.SidecarFileName), "{\"source\":\"pr\"}");

        var probe = new FakePeerInstallProbe(new Dictionary<string, PeerProbeResult>
        {
            [binary] = new PeerProbeResult.Failed("simulated peer hang"),
        });

        using var _ = new EnvVarOverride("HOME", workspace.WorkspaceRoot.FullName);
        using var __ = new EnvVarOverride("USERPROFILE", workspace.WorkspaceRoot.FullName);

        var discovery = NewDiscovery(probe);
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        var row = results.Single(r =>
            string.Equals(r.CanonicalPath, binary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(InstallationInfoStatus.NotProbed, row.Status);
        Assert.Equal("pr", row.Route);
        Assert.Contains("simulated peer hang", row.StatusReason!);
    }

    [Fact]
    public async Task DiscoverAllAsync_RunningCliIsAlwaysFirst()
    {
        // Self must appear first regardless of what walks find — both for
        // the table display contract ("(current)" marker) and to keep peer
        // dedup deterministic.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var _ = new EnvVarOverride("HOME", workspace.WorkspaceRoot.FullName);
        using var __ = new EnvVarOverride("USERPROFILE", workspace.WorkspaceRoot.FullName);

        var discovery = NewDiscovery(new FakePeerInstallProbe());
        var results = await discovery.DiscoverAllAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Equal(InstallationInfoStatus.Ok, results[0].Status);
        var canonicalSelf = ResolveCanonicalProcessPath();
        Assert.Equal(canonicalSelf, results[0].CanonicalPath, StringComparer.OrdinalIgnoreCase);
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

    private static InstallationDiscovery NewDiscovery(FakePeerInstallProbe probe)
    {
        return new InstallationDiscovery(
            channelReader: new FakeIdentityChannelReader("local"),
            sidecarReader: new InstallSidecarReader(),
            peerProbe: probe,
            logger: NullLogger<InstallationDiscovery>.Instance);
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
        return path;
    }
}

/// <summary>
/// Restores an environment variable to its prior value on dispose. Used
/// in DiscoverAll tests to point the discovery walk at a controlled
/// <c>HOME</c> / <c>USERPROFILE</c> / <c>PATH</c> sandbox.
/// </summary>
internal sealed class EnvVarOverride : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvVarOverride(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previous);
    }
}
