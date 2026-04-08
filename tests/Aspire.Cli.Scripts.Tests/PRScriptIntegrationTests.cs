// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Integration tests that use real PR builds from GitHub.
/// These tests require gh CLI and query actual PR artifacts.
/// Note: These tests use --dry-run to avoid actual downloads while still
/// validating the complete workflow including PR discovery and artifact queries.
/// 
/// These tests are marked with Trait("Category", "integration") and are excluded from
/// default test runs. Run them on-demand with: --filter-trait "Category=integration"
/// 
/// These tests are disabled in CI (ActiveIssue) and only run on-demand locally.
/// </summary>
[RequiresGHCli]
[Trait("Category", "integration")]
[ActiveIssue("https://github.com/dotnet/aspire/issues/12000", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningOnCI))]
public class PRScriptIntegrationTests : IClassFixture<RealGitHubPRFixture>
{
    private readonly RealGitHubPRFixture _prFixture;
    private readonly ITestOutputHelper _testOutput;

    public PRScriptIntegrationTests(RealGitHubPRFixture prFixture, ITestOutputHelper testOutput)
    {
        _prFixture = prFixture;
        _testOutput = testOutput;
    }

    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    [Fact]
    public async Task ShellScript_WithRealPR_DryRun_Succeeds()
    {
        // Skip if GH_TOKEN is not available
        if (_prFixture.PRNumber == 0)
        {
            _testOutput.WriteLine("Skipping: GH_TOKEN not available or no suitable PR found");
            return;
        }

        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput, forceShowBuildOutput: true);
        
        var result = await cmd.ExecuteAsync(
            _prFixture.PRNumber.ToString(),
            "--dry-run",
            "--run-id", _prFixture.RunId.ToString());

        result.EnsureSuccessful();
        Assert.Contains(_prFixture.PRNumber.ToString(), result.Output);
    }

    [RequiresTools(["pwsh"])]
    [Fact]
    public async Task PowerShellScript_WithRealPR_WhatIf_Succeeds()
    {
        // Skip if GH_TOKEN is not available
        if (_prFixture.PRNumber == 0)
        {
            _testOutput.WriteLine("Skipping: GH_TOKEN not available or no suitable PR found");
            return;
        }

        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.ps1", env, _testOutput, forceShowBuildOutput: true);
        
        var result = await cmd.ExecuteAsync(
            "-PRNumber", _prFixture.PRNumber.ToString(),
            "-WhatIf",
            "-WorkflowRunId", _prFixture.RunId.ToString());

        result.EnsureSuccessful();
        Assert.Contains(_prFixture.PRNumber.ToString(), result.Output);
    }

    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    [Fact]
    public async Task ShellScript_WithRealPR_DiscoverRunId_DryRun_Succeeds()
    {
        // Skip if GH_TOKEN is not available
        if (_prFixture.PRNumber == 0)
        {
            _testOutput.WriteLine("Skipping: GH_TOKEN not available or no suitable PR found");
            return;
        }

        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.sh", env, _testOutput, forceShowBuildOutput: true);
        
        // Test automatic run ID discovery by only passing PR number
        var result = await cmd.ExecuteAsync(
            _prFixture.PRNumber.ToString(),
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains(_prFixture.PRNumber.ToString(), result.Output);
    }

    [RequiresTools(["pwsh"])]
    [Fact]
    public async Task PowerShellScript_WithRealPR_DiscoverRunId_WhatIf_Succeeds()
    {
        // Skip if GH_TOKEN is not available
        if (_prFixture.PRNumber == 0)
        {
            _testOutput.WriteLine("Skipping: GH_TOKEN not available or no suitable PR found");
            return;
        }

        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.ps1", env, _testOutput, forceShowBuildOutput: true);
        
        // Test automatic run ID discovery by only passing PR number
        var result = await cmd.ExecuteAsync(
            "-PRNumber", _prFixture.PRNumber.ToString(),
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(_prFixture.PRNumber.ToString(), result.Output);
    }
}
