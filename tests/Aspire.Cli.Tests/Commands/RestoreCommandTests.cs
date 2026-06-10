// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Configuration;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class RestoreCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RestoreCommand_WithDotNetAppHost_RunsDotNetRestore()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var restoreCalled = false;
        string? capturedProjectFilePath = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, (Action<CliServiceCollectionTestOptions>?)(options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RestoreAsyncCallback = (projectFilePath, _, _) =>
                {
                    restoreCalled = true;
                    capturedProjectFilePath = projectFilePath.FullName;
                    return (int)Aspire.Cli.CliExitCodes.Success;
                }
            };
        }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);
        Assert.True(restoreCalled);
        Assert.Equal(appHostFile.FullName, capturedProjectFilePath);
    }

    [Fact]
    public async Task RestoreCommand_WithDotNetAppHostAndMissingSdk_ReturnsSdkNotInstalled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var restoreCalled = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, (Action<CliServiceCollectionTestOptions>?)(options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => ((bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion))(false, null, "9.0.302")
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RestoreAsyncCallback = (_, _, _) =>
                {
                    restoreCalled = true;
                    return (int)Aspire.Cli.CliExitCodes.Success;
                }
            };
        }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.SdkNotInstalled, exitCode);
        Assert.False(restoreCalled);
    }

    [Fact]
    public async Task RestoreCommand_WithCliManagedSingleFileAppHost_GeneratesModuleBeforeRestore()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHostFile.FullName, """
            #:project .aspire/modules/Aspire.csproj

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "packages": {
                "Aspire.Hosting.Redis": "13.2.1"
              }
            }
            """);

        var buildCalled = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.CSharpCliManagedAppHostEnabled];
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFilePath, _, _, _) =>
                {
                    buildCalled = true;

                    // The CLI-managed restore path builds the integration module project
                    // (.aspire/modules/Aspire.csproj), not the user's apphost.cs.
                    var moduleProjectPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.csproj");
                    var targetsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.targets");
                    Assert.Equal(moduleProjectPath, projectFilePath.FullName);
                    Assert.True(File.Exists(moduleProjectPath));
                    Assert.True(File.Exists(targetsPath));
                    Assert.Contains("Aspire.Hosting.Redis", File.ReadAllText(targetsPath));

                    // Stub out the closure-manifest files MSBuild would emit so the restorer
                    // post-processes them into a probe manifest.
                    var workingDir = IntegrationClosureRestorer.GetOrCreateWorkingDirectory(appHostFile);
                    var restoreDir = Path.Combine(workingDir.FullName, IntegrationClosureRestorer.IntegrationRestoreFolderName);
                    Directory.CreateDirectory(restoreDir);
                    File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureRestorer.ClosureSourcesFileName), string.Empty);
                    File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureRestorer.ClosureMetadataFileName), string.Empty);
                    File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureRestorer.ClosureTargetsFileName), string.Empty);
                    File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureRestorer.ProjectRefAssemblyNamesFileName), string.Empty);

                    return (int)Aspire.Cli.CliExitCodes.Success;
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);
        Assert.True(buildCalled);
    }
}
