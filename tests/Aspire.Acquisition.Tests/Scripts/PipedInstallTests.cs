// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests the documented piped install patterns (<c>curl | bash -s</c> and <c>irm | iex</c>)
/// against a local HTTP server hosting the acquisition scripts. This validates the full
/// pipeline: HTTP download → shell pipe → script execution, catching issues like
/// unbound-variable errors, encoding problems, or content-type mismatches.
/// </summary>
public class PipedInstallTests : IClassFixture<ScriptHostFixture>
{
    private readonly ScriptHostFixture _host;
    private readonly ITestOutputHelper _testOutput;

    public PipedInstallTests(ScriptHostFixture host, ITestOutputHelper testOutput)
    {
        _host = host;
        _testOutput = testOutput;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Bash: curl -fsSL <url> | bash -s -- <args>
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash + curl")]
    public async Task CurlPipeToBash_ReleaseScript_HelpWorks()
    {
        // This is the documented install pattern from get-aspire-cli.sh line 5:
        //   curl -sSL <url>/get-aspire-cli.sh | bash -s -- [OPTIONS]
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("bash", _testOutput, label: "curl|bash:release");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        // Wrap the entire command in quotes so .NET's argument parser keeps it
        // as a single argv entry for bash -c (otherwise spaces split the arg).
        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"curl -fsSL {_host.BaseUrl}/get-aspire-cli.sh | bash -s -- --help\"");

        result.EnsureSuccessful();
        Assert.Contains("Aspire CLI", result.Output);
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash + curl")]
    public async Task CurlPipeToBash_PRScript_HelpWorks()
    {
        // Documented pattern from get-aspire-cli-pr.sh line 88:
        //   curl -fsSL <url>/get-aspire-cli-pr.sh | bash -s -- <PR_NUMBER>
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("bash", _testOutput, label: "curl|bash:pr");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"curl -fsSL {_host.BaseUrl}/get-aspire-cli-pr.sh | bash -s -- --help\"");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash + curl")]
    public async Task CurlPipeToBash_ReleaseScript_InvalidQualityFails()
    {
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("bash", _testOutput, label: "curl|bash:release-err");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"curl -fsSL {_host.BaseUrl}/get-aspire-cli.sh | bash -s -- --quality bogus\"");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash + curl")]
    public async Task CurlPipeToBash_ReleaseScript_ArgsWithSpacesWork()
    {
        // Validates that arguments containing spaces are properly handled
        // when the script is piped via curl | bash -s --
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "path with spaces", "cli");
        using var cmd = new ToolCommand("bash", _testOutput, label: "curl|bash:spaces");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        // Use --dry-run if available, or --help with --install-path to verify argument parsing
        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"curl -fsSL {_host.BaseUrl}/get-aspire-cli.sh | bash -s -- --install-path '{installPath}' --help\"");

        // --help should still succeed even with --install-path set
        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Bash: wget -qO- <url> | bash (alternative pipe pattern)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash")]
    public async Task CatPipeToBash_ReleaseScript_HelpWorks()
    {
        // Tests the pipe mechanism without requiring curl/wget — uses cat to
        // simulate reading from stdin, which is the same codepath as curl | bash
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");
        var scriptPath = Path.Combine(repoRoot, ScriptPaths.ReleaseShell);

        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("bash", _testOutput, label: "cat|bash:release");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"cat '{scriptPath}' | bash -s -- --help\"");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash piped install tests require bash")]
    public async Task CatPipeToBash_PRScript_HelpWorks()
    {
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");
        var scriptPath = Path.Combine(repoRoot, ScriptPaths.PRShell);

        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("bash", _testOutput, label: "cat|bash:pr");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync(
            "-c",
            $"\"cat '{scriptPath}' | bash -s -- --help\"");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  PowerShell: iex "& { $(irm <url>) } <args>"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IrmPipeToIex_ReleaseScript_HelpWorks()
    {
        // This is the documented install pattern from get-aspire-cli.ps1 line 174:
        //   iex "& { $(irm <url>) }"
        //   iex "& { $(irm <url>) } -Quality staging"
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("pwsh", _testOutput, label: "irm|iex:release");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        // Outer quotes group the entire expression as a single process argument;
        // inner \" are literal quotes that survive Windows command-line parsing,
        // so pwsh -Command receives: iex "& { $(irm URL) } -Help"
        var result = await cmd.ExecuteAsync(
            "-NoProfile",
            "-Command",
            $"\"iex \\\"& {{ $(irm {_host.BaseUrl}/get-aspire-cli.ps1) }} -Help\\\"\"");

        result.EnsureSuccessful();
        Assert.Contains("Aspire CLI", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IrmPipeToIex_PRScript_HelpWorks()
    {
        // Documented pattern from get-aspire-cli-pr.ps1 line 74:
        //   iex "& { $(irm <url>) } <PR_NUMBER>"
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("pwsh", _testOutput, label: "irm|iex:pr");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync(
            "-NoProfile",
            "-Command",
            $"\"iex \\\"& {{ $(irm {_host.BaseUrl}/get-aspire-cli-pr.ps1) }} -Help\\\"\"");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IrmPipeToIex_ReleaseScript_InvalidQualityFails()
    {
        using var env = new TestEnvironment();
        using var cmd = new ToolCommand("pwsh", _testOutput, label: "irm|iex:release-err");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(30));

        // Wrap in try/catch to propagate the ValidateSet error as a non-zero exit,
        // since bare iex swallows parameter-binding exceptions without setting $LASTEXITCODE.
        var result = await cmd.ExecuteAsync(
            "-NoProfile",
            "-Command",
            $"\"try {{ iex \\\"& {{ $(irm {_host.BaseUrl}/get-aspire-cli.ps1) }} -Quality bogus\\\" }} catch {{ exit 1 }}\"");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IrmPipeToIex_ReleaseScript_ArgsWithSpacesWork()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "path with spaces", "cli");
        using var cmd = new ToolCommand("pwsh", _testOutput, label: "irm|iex:spaces");
        cmd.WithEnvironmentVariable("HOME", env.MockHome);
        cmd.WithEnvironmentVariable("USERPROFILE", env.MockHome);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        // Quote the path in the iex expression
        var result = await cmd.ExecuteAsync(
            "-NoProfile",
            "-Command",
            $"\"iex \\\"& {{ $(irm {_host.BaseUrl}/get-aspire-cli.ps1) }} -InstallPath '{installPath}' -Help\\\"\"");

        result.EnsureSuccessful();
        Assert.Contains("Aspire CLI", result.Output);
    }
}
