// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// End-to-end coverage for <see cref="ScriptPaths.PRShell"/> through the real
/// download + extract + sidecar-write flow. mock-gh stands in for GitHub's
/// `gh run download` and copies a synthetic per-RID tarball into the script's
/// download directory; the script then proceeds through extraction, install,
/// and sidecar write as it would in production.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class PRScriptInstallE2ETests(ITestOutputHelper testOutput)
{
    private static readonly string s_scriptPath = ScriptPaths.PRShell;
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    public async Task PRScript_InstallsCliAndWritesRouteSidecar()
    {
        using var env = new TestEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;

        var stageDir = Path.Combine(env.TempDirectory, "stage");
        Directory.CreateDirectory(stageDir);

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(stageDir, platform: "linux-x64");

        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);

        var installPrefix = Path.Combine(env.TempDirectory, "install");
        Directory.CreateDirectory(installPrefix);

        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        // mock-gh's `gh run download` copies this archive into the script's
        // download dir, so the script's normal extract + install path runs
        // end-to-end against a real tarball.
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_ARCHIVE_SOURCE", archive.ArchivePath);

        var result = await cmd.ExecuteAsync(
            "16817",
            "--run-id", "987654321",
            "--install-path", installPrefix,
            "--skip-extension",
            "--skip-path",
            "--os", "linux",
            "--arch", "x64");

        result.EnsureSuccessful();

        var binDir = Path.Combine(installPrefix, "dogfood", "pr-16817", "bin");
        Assert.True(File.Exists(Path.Combine(binDir, "aspire")), $"CLI binary missing under {binDir}");

        var sidecarPath = Path.Combine(binDir, ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Route sidecar missing at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath, cancellationToken);
        _testOutput.WriteLine($"Sidecar content after install: {sidecarContent}");
        Assert.Contains("\"source\"", sidecarContent);
        Assert.Contains("\"pr\"", sidecarContent);
    }
}
