// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Tests for the bash PR script (get-aspire-cli-pr.sh).
/// These tests validate parameter handling with mock gh CLI.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class PRScriptShellTests
{
    private readonly ITestOutputHelper _testOutput;

    public PRScriptShellTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    private async Task<ScriptToolCommand> CreateCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingPRNumber_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task DryRunWithPRNumber_ShowsSteps()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("12345", result.Output);
    }

    [Fact]
    public async Task CustomInstallPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--install-path", customPath);

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task RunIdParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--run-id", "987654321");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task OSOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--os", "linux");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--arch", "x64");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task HiveOnlyFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--hive-only");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task SkipExtensionFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--skip-extension");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task UseInsidersFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--use-insiders");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task VerboseFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task KeepArchiveFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--keep-archive");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task MultipleFlags_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--dry-run",
            "--install-path", customPath,
            "--os", "linux",
            "--arch", "x64",
            "--skip-extension",
            "--skip-path",
            "--keep-archive",
            "--verbose");

        result.EnsureSuccessful();
    }
}
