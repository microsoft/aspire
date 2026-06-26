// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
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
        // The probe's contract is "do not touch a parseable sidecar even if
        // its source string is unrecognized"; if it ever rewrites
        // unconditionally, this distinctive payload would be overwritten
        // with the probe's canonical {"source":"winget"} content and the
        // assertion below would fail — independent of any filesystem
        // timestamp resolution.
        //
        // Corrupt-sidecar self-heal is covered separately in
        // Run_OverwritesMalformedSidecar_WhenRegistryClaimsAspire below.
        const string preSeededContent = "{\"source\":\"winget-pre-seeded\"}";
        File.WriteAllText(sidecarPath, preSeededContent);

        // Even with the registry asserting winget, a parseable existing
        // sidecar must not be touched.
        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        probe.Run(realProcessPath);

        Assert.Equal(preSeededContent, File.ReadAllText(sidecarPath));
    }

    [Theory]
    // Malformed JSON: never parses, the probe's old File.Exists guard
    // skipped it and left the install permanently un-identified.
    [InlineData("this is not json {{{ broken")]
    // Empty object: parseable JSON but no `source` field. The reader's
    // ReadSourceField returns null for this shape, so the probe treats it
    // as "needs overwrite" too.
    [InlineData("{}")]
    // Truncated mid-write: representative of a crash during atomic-rename
    // or a partial copy. JsonDocument.Parse throws JsonException.
    [InlineData("{\"sour")]
    public void Run_OverwritesMalformedSidecar_WhenRegistryClaimsAspire(string corruptContent)
    {
        // Regression: the probe used to early-return on File.Exists with no
        // validation, so a corrupted .aspire-install.json (truncated write,
        // crashed mid-rename, manual edit) wedged the install permanently
        // — every subsequent `aspire` invocation saw `route` as null and
        // BundleService fell back to the sidecar-less default. Self-heal
        // is cheap (one JsonDocument.Parse per cold start when the sidecar
        // exists) and bounds the worst-case corruption window to a single
        // CLI invocation.
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        File.WriteAllText(sidecarPath, corruptContent);

        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        probe.Run(realProcessPath);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("winget", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Run_DoesNotOverwriteForeignSidecar_EvenWhenRegistryClaimsAspire()
    {
        // Sibling guarantee to Run_OverwritesMalformedSidecar_*: if a sidecar
        // is parseable and its `source` field is a non-empty string (any
        // string — "script", "brew", "pr", an unknown future route, etc.),
        // the probe must NOT clobber it. The probe owns only the winget
        // detection lane; other install routes write their own sidecars.
        using var workspace = new TestTempDirectory();
        var realProcessPath = Path.Combine(workspace.Path, "aspire.exe");
        var sidecarPath = Path.Combine(workspace.Path, SidecarFileName);
        const string scriptSidecar = "{\"source\":\"script\"}";
        File.WriteAllText(sidecarPath, scriptSidecar);

        var probe = new WingetFirstRunProbe(new FakeWindowsRegistryReader(claim: true), NullLogger<WingetFirstRunProbe>.Instance);
        probe.Run(realProcessPath);

        Assert.Equal(scriptSidecar, File.ReadAllText(sidecarPath));
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

    [Fact]
    public void DescribeSelfSafely_RunsWingetFirstRunProbe()
    {
        // Regression: previously DoctorCommand's --self fast-path called
        // DescribeSelfSafely without the probe, so a fresh winget install
        // reported route=null until some other CLI command (a plain
        // `aspire doctor` without --self, `aspire restore`, etc.) happened
        // to invoke the probe. The PR's verify-winget-install-detection.ps1
        // calls `aspire doctor --format json --self` and silently failed
        // on assertion #1 (route == "winget") on a fresh install. Locking
        // in the priming contract here prevents the --self path from
        // diverging from the discovery path again.
        var reader = new RecordingFakeRegistryReader(claim: false);
        var probe = new WingetFirstRunProbe(reader, NullLogger<WingetFirstRunProbe>.Instance);
        var discovery = new FakeInstallationDiscovery(self: new InstallationInfo
        {
            Path = "/fake/aspire",
            Version = "0.0.0-test",
            Status = InstallationInfoStatus.Ok,
            PathStatus = InstallationPathStatus.Active,
        });

        var result = InstallationInfoOutput.DescribeSelfSafely(discovery, probe, NullLogger.Instance);

        Assert.Single(result);
        // The probe always queries the registry reader exactly once when the
        // sidecar is missing — there is no OS guard inside the probe itself;
        // the platform short-circuit lives in the real WindowsRegistryReader.
        // The fake reader is happy to be queried on any OS, so the assertion
        // is platform-agnostic: a single query proves DescribeSelfSafely
        // invoked the probe rather than skipping it.
        Assert.Equal(1, reader.QueryCount);
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
