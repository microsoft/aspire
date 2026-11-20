// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class MacOSStaplerRetryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("success")]
    [InlineData("transient-success")]
    [InlineData("exhausted")]
    [InlineData("nontransient")]
    [InlineData("changed-error")]
    [InlineData("invalid-arguments")]
    [RequiresTools(["node", "bash"])]
    public async Task MacOSStaplerOperationsRetryOnlyConnectionLoss(string scenario)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Production stapler wrapper uses Unix commands.");
        using var command = new NodeCommand(output).WithTimeout(TimeSpan.FromMinutes(1));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "Pipelines", "macos-stapler-retry.harness.js"),
            RepoRoot.Path, scenario);

        result.EnsureSuccessful();
    }
}
