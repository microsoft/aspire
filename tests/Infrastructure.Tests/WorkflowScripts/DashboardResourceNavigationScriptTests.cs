// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class DashboardResourceNavigationScriptTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ResourceTabOverflow.test.mjs")]
    [InlineData("ResourcePicker.test.mjs")]
    [RequiresTools(["node"])]
    public async Task ResourceNavigationScriptPasses(string script)
    {
        using var command = new NodeCommand(output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(60));
        var result = await command.ExecuteScriptAsync(Path.Combine(RepoRoot.Path,
            "tests", "Aspire.Dashboard.Components.Tests", "JavaScript", script));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResourcesLayoutInitRegistersAndDisposesHandlers()
    {
        using var command = new NodeCommand(output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(60));
        var result = await command.ExecuteScriptAsync(Path.Combine(RepoRoot.Path,
            "tests", "Aspire.Dashboard.Components.Tests", "JavaScript", "ResourcesLayoutInit.test.mjs"));

        Assert.Equal(0, result.ExitCode);
    }
}
