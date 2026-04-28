// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public sealed class TypeScriptAppHostToolingCheckTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenConfiguredToolchainIsAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"packageManager\": \"bun@1.2.0\" }");

        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: command => command.Equals("bun", StringComparison.OrdinalIgnoreCase) ? "/usr/bin/bun" : null);

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Contains("bun", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenConfiguredToolchainIsMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"packageManager\": \"bun@1.2.0\" }");

        var check = CreateCheck(workspace, appHostFile, commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal("https://bun.sh/docs/installation", result.Link);
        Assert.Contains("'bun'", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenNoTypeScriptAppHostExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateCheck(workspace, appHostFile: null, commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    private static FileInfo CreateTypeScriptAppHost(TemporaryWorkspace workspace, string packageJsonContent)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        File.WriteAllText(appHostPath, "await Promise.resolve();");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), packageJsonContent);
        return new FileInfo(appHostPath);
    }

    private static TypeScriptAppHostToolingCheck CreateCheck(
        TemporaryWorkspace workspace,
        FileInfo? appHostFile,
        Func<string, string?> commandResolver)
    {
        var projectLocator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult(appHostFile)
        };

        return new TypeScriptAppHostToolingCheck(
            projectLocator,
            new TestLanguageDiscovery(s_typeScriptLanguage),
            CreateExecutionContext(workspace),
            NullLogger<TypeScriptAppHostToolingCheck>.Instance,
            commandResolver);
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log");
}
