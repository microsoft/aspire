// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class DoctorCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoctorCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Help should return success
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DoctorCommand_SkipsPolyglotChecks_WhenNoTypeScriptAppHostIsPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var checker = provider.GetRequiredService<IEnvironmentChecker>();
        var results = await checker.CheckAllAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.DoesNotContain(results, r => r.Category == "polyglot");
    }

    [Fact]
    public async Task DoctorCommand_RunsPolyglotChecks_WhenTypeScriptAppHostIsPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), """{ "packageManager": "yarn@1.22.22" }""");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var checker = provider.GetRequiredService<IEnvironmentChecker>();
        var results = await checker.CheckAllAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var polyglotResults = results.Where(r => r.Category == "polyglot").ToList();
        Assert.NotEmpty(polyglotResults);
        Assert.Contains(polyglotResults, r => r.Name == "package-manager-yarn" && r.Status == EnvironmentCheckStatus.Warning);
    }
}
