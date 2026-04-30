// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests that verify PowerShell scripts can have their functions loaded safely
/// via AST extraction without executing main.
/// </summary>
[RequiresTools(["pwsh"])]
public class PSSourceabilityTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    public async Task ReleaseScript_CanLoadFunctions_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleasePowerShell,
            "Write-Output 'loaded-ok'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("loaded-ok", result.Output);
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PRScript_CanLoadFunctions_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            "Write-Output 'loaded-ok'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("loaded-ok", result.Output);
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
