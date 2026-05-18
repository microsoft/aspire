// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils.EnvironmentChecks;

public class TypeScriptAppHostPackageManagerCheckTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenNoTypeScriptAppHostPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new TypeScriptAppHostPackageManagerCheck(executionContext, NullLogger<TypeScriptAppHostPackageManagerCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_EmitsYarnClassicWarning_WhenYarnClassicDetected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), """{ "packageManager": "yarn@1.22.22" }""");

        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new TypeScriptAppHostPackageManagerCheck(executionContext, NullLogger<TypeScriptAppHostPackageManagerCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("polyglot", result.Category);
        Assert.Equal("package-manager-yarn", result.Name);
        Assert.NotNull(result.Link);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task CheckAsync_AssignsPolyglotCategoryToAllResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{}");

        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new TypeScriptAppHostPackageManagerCheck(executionContext, NullLogger<TypeScriptAppHostPackageManagerCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("polyglot", r.Category));
    }

    [Fact]
    public void Order_IsAfterContainerRuntime()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new TypeScriptAppHostPackageManagerCheck(executionContext, NullLogger<TypeScriptAppHostPackageManagerCheck>.Instance);

        Assert.True(check.Order >= 45);
    }

    private static CliExecutionContext BuildExecutionContext(DirectoryInfo workingDirectory)
    {
        var aspireRoot = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire"));
        var hives = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "hives"));
        var cache = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "cache"));
        var sdks = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks"));
        var logs = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "logs"));
        var logFile = Path.Combine(logs.FullName, "test.log");

        return new CliExecutionContext(workingDirectory, hives, cache, sdks, logs, logFile);
    }
}
