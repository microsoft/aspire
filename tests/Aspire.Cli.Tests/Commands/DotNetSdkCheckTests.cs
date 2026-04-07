// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class DotNetSdkCheckTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CheckAsync_SkipsCheck_WhenNoAppHostFound()
    {
        // No apphost in settings — skip .NET SDK check entirely
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace,
            sdkCheckResult: (false, null, "10.0.100"),
            languageDiscovery: new TestLanguageDiscovery());

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_SkipsCheck_WhenNonDotNetAppHostFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace,
            appHostFileName: "apphost.ts",
            sdkCheckResult: (false, null, "10.0.100"));

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_RunsCheck_WhenDotNetAppHostFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace, appHostFileName: "MyAppHost.csproj");

        var results = await check.CheckAsync().DefaultTimeout();
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenDotNetAppHostFound_AndSdkNotInstalled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace,
            appHostFileName: "MyAppHost.csproj",
            sdkCheckResult: (false, null, "10.0.100"));

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Fail, results[0].Status);
        Assert.Contains(".NET SDK not found", results[0].Message);
    }

    [Fact]
    public async Task CheckAsync_SkipsCheck_WhenNoSettingsFileExists()
    {
        // No settings.json — can't determine language, skip .NET check
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_SkipsCheck_WhenLanguageNotRecognized()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace, appHostFileName: "unknown.xyz");

        var results = await check.CheckAsync().DefaultTimeout();

        // Unrecognized file — skip .NET check
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("MyAppHost.csproj", true)]
    [InlineData("apphost.cs", true)]
    [InlineData("readme.txt", false)]
    [InlineData("src/MyAppHost.csproj", true)]
    [InlineData("src/AppHost/apphost.cs", true)]
    [InlineData("src/deep/nested/readme.txt", false)]
    [InlineData("a/b/c/d/e/f/apphost.cs", false)]
    public async Task CheckAsync_FallsBackToFileSystemScan_WhenNoSettingsFile(string relativePath, bool shouldRunCheck)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var check = CreateDotNetSdkCheck(workspace);

        // Create the file on disk (with nested directories) so FindFirstFile can discover it
        var filePath = Path.Combine(workspace.WorkspaceRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "");

        var results = await check.CheckAsync().DefaultTimeout();

        if (shouldRunCheck)
        {
            Assert.Single(results);
            Assert.Equal(EnvironmentCheckStatus.Pass, results[0].Status);
        }
        else
        {
            Assert.Empty(results);
        }
    }

    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log");

    private static DotNetSdkCheck CreateDotNetSdkCheck(
        TemporaryWorkspace workspace,
        string? appHostFileName = null,
        (bool Success, string? HighestVersion, string MinimumRequired)? sdkCheckResult = null,
        ILanguageDiscovery? languageDiscovery = null)
    {
        var appHostFile = appHostFileName is not null
            ? new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, appHostFileName))
            : null;

        var sdkInstaller = new TestDotNetSdkInstaller();
        if (sdkCheckResult is var (success, highest, minimum))
        {
            sdkInstaller.CheckAsyncCallback = _ => (success, highest, minimum);
        }

        var projectLocator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult(appHostFile)
        };

        var executionContext = CreateExecutionContext(workspace);

        return new DotNetSdkCheck(
            sdkInstaller,
            projectLocator,
            languageDiscovery ?? new TestLanguageDiscovery(s_typeScriptLanguage),
            executionContext,
            NullLogger<DotNetSdkCheck>.Instance);
    }
}
