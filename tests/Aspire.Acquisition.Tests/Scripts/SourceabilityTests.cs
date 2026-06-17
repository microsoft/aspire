// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests that verify bash scripts can be sourced safely without executing main.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class SourceabilityTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    public async Task ReleaseScript_CanBeSourced_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleaseShell,
            "echo sourced-ok",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("sourced-ok", result.Output);
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PRScript_CanBeSourced_WithoutExecutingMain()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            "echo sourced-ok",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("sourced-ok", result.Output);
        Assert.DoesNotContain("Downloading", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
