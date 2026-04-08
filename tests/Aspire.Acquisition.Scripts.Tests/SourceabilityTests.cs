// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Tests that verify bash scripts can be sourced safely without executing main,
/// and that direct execution still works after the refactor.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class SourceabilityTests
{
    private readonly ITestOutputHelper _testOutput;

    public SourceabilityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public async Task ReleaseScript_CanBeSourced_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.sh",
            "echo sourced-ok",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("sourced-ok", result.Output);
        // Verify sourcing did not trigger downloads or installation prompts
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PRScript_CanBeSourced_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli-pr.sh",
            "echo sourced-ok",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("sourced-ok", result.Output);
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseScript_DirectExecution_HelpStillWorks()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PRScript_DirectExecution_HelpStillWorks()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseScript_SourceAndCallFunction_Works()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.sh",
            "map_quality_to_channel 'release'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("stable", result.Output);
    }

    [Fact]
    public async Task PRScript_SourceAndCallFunction_Works()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli-pr.sh",
            "get_runtime_identifier 'linux' 'x64'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("linux-x64", result.Output);
    }
}
