// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

public class WingetRegistryProbeTests
{
    private const string SidecarFileName = ".aspire-install.json";

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Run_WritesSidecarOnlyWhenRegistryClaimsAspire(bool registryClaim, bool expectSidecar)
    {
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: registryClaim), NullLogger<WingetFirstRunProbe>.Instance);

        probe.Run(realProcessPath);

        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        Assert.Equal(expectSidecar, File.Exists(sidecarPath));

        if (expectSidecar)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal("winget", doc.RootElement.GetProperty("source").GetString());
        }
        else
        {
            Assert.Empty(Directory.GetFiles(workspace.Path));
        }
    }

    [Fact]
    public void Run_IsIdempotent_OnSecondRun()
    {
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        // Pre-seed with a distinctive payload no real producer would write.
        // The probe's contract is "do not touch an existing sidecar"; if it
        // ever rewrites unconditionally, this distinctive payload would be
        // overwritten with the probe's canonical {"source":"winget"} content
        // and the assertion below would fail — independent of any filesystem
        // timestamp resolution.
        const string preSeededContent = "{\"source\":\"winget-pre-seeded\"}";
        File.WriteAllText(sidecarPath, preSeededContent);

        // Even with the registry asserting winget, an existing sidecar must
        // not be touched.
        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        probe.Run(realProcessPath);

        Assert.Equal(preSeededContent, File.ReadAllText(sidecarPath));
    }

    [Fact]
    public async Task Run_ConcurrentInvocations_ProduceSingleValidSidecar()
    {
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        var errors = new ConcurrentBag<Exception>();

        var tasks = new Task[16];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    probe.Run(realProcessPath);
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
        Assert.Equal("winget", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Run_ForwardsRealProcessPathToRegistryReader()
    {
        // Regression: the probe previously re-read Environment.ProcessPath
        // internally, which on Windows can yield the winget command-alias
        // symlink path under %LOCALAPPDATA%\Microsoft\WinGet\Links rather
        // than the resolved binary path under %LOCALAPPDATA%\Microsoft\WinGet\Packages.
        // The matcher's InstallLocation containment check then misses, the
        // sidecar is never written, and the bundle ends up under ~/.aspire
        // — the exact regression the probe exists to prevent. Locking in the
        // pass-through contract here ensures any future "convenience" change
        // that re-introduces a raw Environment.ProcessPath read fails this test.
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var reader = new RecordingFakeRegistryReader(claim: true);
        var probe = new WingetFirstRunProbe(reader, NullLogger<WingetFirstRunProbe>.Instance);

        probe.Run(realProcessPath);

        Assert.Equal(realProcessPath, reader.LastQueriedPath);
        Assert.Equal(1, reader.QueryCount);
    }

    [Fact]
    public void Run_WritesSidecarNextToRealProcessPath_NotElsewhere()
    {
        // Companion to Run_ForwardsRealProcessPathToRegistryReader: the
        // sidecar location is derived from the path argument, so a caller
        // that passes a resolved path also gets the sidecar in the right
        // (resolved) directory.
        using var workspace = new TestTempDirectory();
        var realBinaryDir = Path.Combine(workspace.Path, "package-dir");
        Directory.CreateDirectory(realBinaryDir);
        var realProcessPath = Path.Combine(realBinaryDir, "aspire.exe");

        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        probe.Run(realProcessPath);

        Assert.True(File.Exists(Path.Combine(realBinaryDir, SidecarFileName)));
        Assert.False(File.Exists(Path.Combine(workspace.Path, SidecarFileName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Run_EmptyOrNullPath_IsNoOp(string? path)
    {
        var reader = new RecordingFakeRegistryReader(claim: true);
        var probe = new WingetFirstRunProbe(reader, NullLogger<WingetFirstRunProbe>.Instance);

        probe.Run(path!);

        Assert.Equal(0, reader.QueryCount);
    }
}

file sealed class FakeWindowsRegistryReader(bool claim) : IWindowsRegistryReader
{
    public bool HasWingetAspireUninstallEntry(string processPath) => claim;
}

file sealed class RecordingFakeRegistryReader(bool claim) : IWindowsRegistryReader
{
    public string? LastQueriedPath { get; private set; }
    public int QueryCount { get; private set; }

    public bool HasWingetAspireUninstallEntry(string processPath)
    {
        LastQueriedPath = processPath;
        QueryCount++;
        return claim;
    }
}
