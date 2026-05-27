// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

public class WingetSidecarBackfillTests
{
    private const string SidecarFileName = ".aspire-install.json";

    [Fact]
    public void EnsureSidecar_WritesWingetSidecar_WhenRegistryClaimsWinget()
    {
        using var workspace = new TestTempDirectory();
        var backfill = new WingetSidecarBackfill(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetSidecarBackfill>.Instance);

        backfill.EnsureSidecar(workspace.Path);

        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        Assert.True(File.Exists(sidecarPath));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("winget", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void EnsureSidecar_WritesBackfilledSentinel_WhenRegistryDoesNotClaimWinget()
    {
        using var workspace = new TestTempDirectory();
        var backfill = new WingetSidecarBackfill(new FakeWindowsRegistryReader(claim: false), NullLogger<WingetSidecarBackfill>.Instance);

        backfill.EnsureSidecar(workspace.Path);

        // The sentinel is the cost-reduction half of the back-fill: writing it
        // is what makes the next EnsureSidecar call short-circuit instead of
        // walking HKCU and HKLM Uninstall hives again.
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        Assert.True(File.Exists(sidecarPath));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetProperty("backfilled").GetBoolean());
        // Intentionally no "source" field — InstallSidecarReader treats the
        // sentinel as Unknown source, wire-identical to a missing sidecar from
        // downstream consumers' perspective.
        Assert.False(doc.RootElement.TryGetProperty("source", out _));
    }

    [Fact]
    public void EnsureSidecar_SkipsRegistryWalk_WhenSidecarExists()
    {
        // This is the central cost-reduction guarantee: once the sidecar exists
        // (real or backfilled sentinel), EnsureSidecar must NOT consult the
        // registry reader. The counting fake fails the test if it's invoked.
        using var workspace = new TestTempDirectory();
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        File.WriteAllText(sidecarPath, "{\"backfilled\":true}");

        var registry = new CountingFakeWindowsRegistryReader(claim: true);
        var backfill = new WingetSidecarBackfill(registry, NullLogger<WingetSidecarBackfill>.Instance);

        backfill.EnsureSidecar(workspace.Path);

        Assert.Equal(0, registry.CallCount);
    }

    [Fact]
    public void EnsureSidecar_DoesNotOverwriteExistingSidecar()
    {
        using var workspace = new TestTempDirectory();
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        // Pre-seed with a distinctive payload no real producer would write.
        // The back-fill's contract is "do not touch an existing sidecar"; if it
        // ever rewrites unconditionally, this distinctive payload would be
        // overwritten with the canonical {"source":"winget"} content and the
        // assertion below would fail — independent of any filesystem timestamp
        // resolution.
        const string preSeededContent = "{\"source\":\"winget-pre-seeded\"}";
        File.WriteAllText(sidecarPath, preSeededContent);

        // Even with the registry asserting winget, an existing sidecar must
        // not be touched.
        var backfill = new WingetSidecarBackfill(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetSidecarBackfill>.Instance);
        backfill.EnsureSidecar(workspace.Path);

        Assert.Equal(preSeededContent, File.ReadAllText(sidecarPath));
    }

    [Fact]
    public void EnsureSidecar_DoesNotPromoteBackfilledSentinel_OnLaterWingetRun()
    {
        // Specific aspect of the "never overwrite" rule: a later run that
        // would have written the positive payload still respects the sentinel
        // from an earlier negative run. If a user really did install via winget
        // after a prior probed-negative run, they pay one row of `source:null`
        // in `aspire --info` until they reinstall or delete the sentinel — a
        // bounded UX cost in exchange for not re-walking the registry every
        // startup.
        using var workspace = new TestTempDirectory();
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        const string sentinelContent = "{\"backfilled\":true}";
        File.WriteAllText(sidecarPath, sentinelContent);

        var backfill = new WingetSidecarBackfill(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetSidecarBackfill>.Instance);
        backfill.EnsureSidecar(workspace.Path);

        Assert.Equal(sentinelContent, File.ReadAllText(sidecarPath));
    }

    [Theory]
    [InlineData(true, "winget")]
    [InlineData(false, null)]
    public async Task EnsureSidecar_ConcurrentInvocations_ProduceSingleValidSidecar(bool registryClaim, string? expectedSource)
    {
        using var workspace = new TestTempDirectory();
        var backfill = new WingetSidecarBackfill(new FakeWindowsRegistryReader(claim: registryClaim), NullLogger<WingetSidecarBackfill>.Instance);
        var errors = new ConcurrentBag<Exception>();

        var tasks = new Task[16];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    backfill.EnsureSidecar(workspace.Path);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Empty(errors);

        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        Assert.True(File.Exists(sidecarPath));

        // Race losers must clean up their temp files.
        var leftovers = Directory.GetFiles(workspace.Path, $"{SidecarFileName}.*.tmp");
        Assert.Empty(leftovers);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        if (expectedSource is null)
        {
            Assert.True(doc.RootElement.GetProperty("backfilled").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("source", out _));
        }
        else
        {
            Assert.Equal(expectedSource, doc.RootElement.GetProperty("source").GetString());
        }
    }
}

file sealed class FakeWindowsRegistryReader(bool claim) : IWindowsRegistryReader
{
    public bool HasWingetAspireUninstallEntry(string processPath) => claim;
}

// Counting variant used to assert that the fast path in EnsureSidecar skips
// the registry walk entirely when a sidecar already exists.
file sealed class CountingFakeWindowsRegistryReader(bool claim) : IWindowsRegistryReader
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public bool HasWingetAspireUninstallEntry(string processPath)
    {
        Interlocked.Increment(ref _callCount);
        return claim;
    }
}
