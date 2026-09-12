// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class DashboardTerminalScriptTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["node"])]
    public async Task TerminalLifecycleAndPackageContract()
    {
        await RunScriptAsync("TerminalView.test.mjs");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TerminalInputDoesNotActivateDashboardShortcuts()
    {
        await RunScriptAsync("KeyboardShortcuts.test.mjs");
    }

    private async Task RunScriptAsync(string script)
    {
        using var command = new NodeCommand(output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(60));
        var result = await command.ExecuteScriptAsync(Path.Combine(RepoRoot.Path,
            "tests", "Aspire.Dashboard.Components.Tests", "JavaScript", script));

        Assert.Equal(0, result.ExitCode);
    }
}
