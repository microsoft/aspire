// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class DashboardResourceGraphScriptTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectionBeforeResourceSnapshotDoesNotThrow()
    {
        using var command = new NodeCommand(output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(60));
        var result = await command.ExecuteScriptAsync(Path.Combine(RepoRoot.Path,
            "tests", "Aspire.Dashboard.Components.Tests", "JavaScript", "ResourceGraph.test.mjs"));

        Assert.Equal(0, result.ExitCode);
    }
}
