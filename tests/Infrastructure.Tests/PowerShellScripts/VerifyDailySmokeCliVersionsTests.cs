// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/verify-daily-smoke-cli-versions.ps1
/// </summary>
public class VerifyDailySmokeCliVersionsTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly string _stepSummaryPath;
    private readonly ITestOutputHelper _output;

    public VerifyDailySmokeCliVersionsTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "verify-daily-smoke-cli-versions.ps1");
        _stepSummaryPath = Path.Combine(_tempDir.Path, "step-summary.md");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SucceedsWhenAllVersionRecordsMatch()
    {
        var versionsDir = CreateVersionsDir();
        WriteVersionRecord(versionsDir, "install-script.env", "SmokeTests.CreateAndRun", "InstallScript", "InstallScript (--quality staging)", "13.3.0+abc123");
        WriteVersionRecord(versionsDir, "dotnet-tool.env", "DotnetToolSmokeTests.CreateAndRun", "DotnetTool", "DotnetTool (staging)", "13.3.0+abc123");

        var result = await RunScript(versionsDir);

        result.EnsureSuccessful("verify-daily-smoke-cli-versions.ps1 failed");
        Assert.Contains("All daily smoke tests installed Aspire CLI version: 13.3.0+abc123", result.Output);
        Assert.False(File.Exists(_stepSummaryPath));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenVersionRecordsDoNotMatch()
    {
        var versionsDir = CreateVersionsDir();
        WriteVersionRecord(versionsDir, "install-script.env", "SmokeTests.CreateAndRun", "InstallScript", "InstallScript (--quality staging)", "13.3.0+abc123");
        WriteVersionRecord(versionsDir, "dotnet-tool.env", "DotnetToolSmokeTests.CreateAndRun", "DotnetTool", "DotnetTool (staging)", "13.4.0-preview.1+def456");

        var result = await RunScript(versionsDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("different Aspire CLI versions", result.Output);
        Assert.Contains("InstallScript (--quality staging) | SmokeTests.CreateAndRun | 13.3.0+abc123", result.Output);
        Assert.Contains("DotnetTool (staging) | DotnetToolSmokeTests.CreateAndRun | 13.4.0-preview.1+def456", result.Output);
        var summary = File.ReadAllText(_stepSummaryPath);
        Assert.Contains("Aspire CLI version consistency check failed", summary);
        Assert.Contains("DotnetTool (staging) | DotnetToolSmokeTests.CreateAndRun | 13.4.0-preview.1+def456", summary);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenVersionRecordDoesNotIncludeVersion()
    {
        var versionsDir = CreateVersionsDir();
        WriteVersionRecord(versionsDir, "missing-version.env", "SmokeTests.CreateAndRun", "InstallScript", "InstallScript (--quality staging)", version: null);

        var result = await RunScript(versionsDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not include a version", result.Output);
        Assert.Contains("InstallScript (--quality staging) | SmokeTests.CreateAndRun | (missing version)", result.Output);
        Assert.Contains("Aspire CLI version consistency check failed", File.ReadAllText(_stepSummaryPath));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenVersionRecordsDirectoryDoesNotExist()
    {
        var versionsDir = Path.Combine(_tempDir.Path, "missing");

        var result = await RunScript(versionsDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version records directory was not created", result.Output);
        Assert.False(File.Exists(_stepSummaryPath));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenVersionRecordsDirectoryIsEmpty()
    {
        var versionsDir = CreateVersionsDir();

        var result = await RunScript(versionsDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No Aspire CLI version records were produced", result.Output);
        Assert.False(File.Exists(_stepSummaryPath));
    }

    private async Task<CommandResult> RunScript(string versionsDir)
    {
        using var command = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("GITHUB_STEP_SUMMARY", _stepSummaryPath);

        return await command.ExecuteAsync("-VersionsDir", $"\"{versionsDir}\"");
    }

    private string CreateVersionsDir()
    {
        var versionsDir = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"), "testresults", "cli-versions");
        Directory.CreateDirectory(versionsDir);
        return versionsDir;
    }

    private static void WriteVersionRecord(string versionsDir, string fileName, string test, string mode, string strategy, string? version)
    {
        var lines = new List<string>
        {
            $"test={test}",
            $"mode={mode}",
            $"strategy={strategy}"
        };

        if (version is not null)
        {
            lines.Add($"version={version}");
            lines.Add($"baseVersion={version.Split('+')[0]}");
        }

        File.WriteAllLines(Path.Combine(versionsDir, fileName), lines);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
