// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public class RecompileAgenticWorkflowsScriptTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["node"])]
    public async Task RecompilationAutomationHandlesDependabotUpdates()
    {
        using var command = new NodeCommand(output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(120));
        var result = await command.ExecuteScriptAsync(Path.Combine(RepoRoot.Path,
            "tests", "Infrastructure.Tests", "WorkflowScripts", "recompile-agentic-workflows.test.js"));

        Assert.Equal(0, result.ExitCode);
    }
}
