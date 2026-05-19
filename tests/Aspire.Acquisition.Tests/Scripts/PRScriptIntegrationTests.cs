// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Integration tests that use real PR builds from GitHub.
/// These tests require gh CLI and query actual PR artifacts.
/// Note: These tests use --dry-run to avoid actual downloads while still
/// validating the complete workflow including PR discovery and artifact queries.
///
/// These tests are marked with Trait("Category", "integration") and are excluded from
/// default test runs. Run them on-demand with: --filter-trait "Category=integration"
///
/// These tests are marked as outerloop since they require real GitHub API access.
/// </summary>
[RequiresTools(["gh"])]
[Trait("Category", "integration")]
[OuterloopTest("Integration tests that require real GitHub API access and gh CLI")]
public class PRScriptIntegrationTests(RealGitHubPRFixture prFixture, ITestOutputHelper testOutput) : IClassFixture<RealGitHubPRFixture>
{
    private readonly RealGitHubPRFixture _prFixture = prFixture;
    private readonly ITestOutputHelper _testOutput = testOutput;

    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    [Fact]
    public async Task ShellScript_WithRealPR_DryRun_Succeeds()
    {
        if (_prFixture.PRNumber == 0)
        {
            Assert.Skip("GH_TOKEN not available or no suitable PR found");
        }

        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

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
        if (_prFixture.PRNumber == 0)
        {
            Assert.Skip("GH_TOKEN not available or no suitable PR found");
        }

        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);

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
        if (_prFixture.PRNumber == 0)
        {
            Assert.Skip("GH_TOKEN not available or no suitable PR found");
        }

        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

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
        if (_prFixture.PRNumber == 0)
        {
            Assert.Skip("GH_TOKEN not available or no suitable PR found");
        }

        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);

        // Test automatic run ID discovery by only passing PR number
        var result = await cmd.ExecuteAsync(
            "-PRNumber", _prFixture.PRNumber.ToString(),
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(_prFixture.PRNumber.ToString(), result.Output);
    }
}
