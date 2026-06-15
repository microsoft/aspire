// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
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
            options.InteractionServiceFactory = _ => new TestInteractionService();
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
        Assert.Equal(PathNormalizer.ResolveToFilesystemPath(appHostFile.FullName), capturedProjectFilePath);
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
    public async Task RestoreCommand_WithDotNetAppHostAndRestoreFailure_DisplaysCapturedOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, (Action<CliServiceCollectionTestOptions>?)(options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RestoreAsyncCallback = (_, invocationOptions, _) =>
                {
                    invocationOptions.StandardOutputCallback?.Invoke("Determining projects to restore...");
                    invocationOptions.StandardErrorCallback?.Invoke("error NU1101: Unable to find package Aspire.Hosting.Bogus.DoesNotExist.");
                    return 1;
                }
            };
        }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var interactionService = (TestInteractionService)provider.GetRequiredService<IInteractionService>();
        Assert.Equal(Aspire.Cli.CliExitCodes.FailedToBuildArtifacts, exitCode);
        Assert.Contains(interactionService.DisplayedLines, line => line is (OutputLineStream.StdOut, "Determining projects to restore..."));
        Assert.Contains(interactionService.DisplayedLines, line => line is (OutputLineStream.StdErr, "error NU1101: Unable to find package Aspire.Hosting.Bogus.DoesNotExist."));
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
                    var moduleProjectPath = PathNormalizer.ResolveToFilesystemPath(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.csproj"));
                    Assert.Equal(moduleProjectPath, projectFilePath.FullName);
                    Assert.True(File.Exists(moduleProjectPath));
                    Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.targets")));
                    Assert.Contains("Aspire.Hosting.Redis", File.ReadAllText(moduleProjectPath));

                    // Stub out the closure-manifest files MSBuild would emit so the restorer
                    // post-processes them into a probe manifest.
                    TestHelpers.WriteEmptyIntegrationClosureFiles(appHostFile);

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

    [Fact]
    public async Task RestoreCommand_WithCliManagedSingleFileAppHostAndRestoreFailure_DisplaysCapturedOutput()
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
                "Aspire.Hosting.Bogus.DoesNotExist": "13.2.1"
              }
            }
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.EnabledFeatures = [KnownFeatures.CSharpCliManagedAppHostEnabled];
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (_, _, invocationOptions, _) =>
                {
                    invocationOptions.StandardOutputCallback?.Invoke("Determining projects to restore...");
                    invocationOptions.StandardErrorCallback?.Invoke("error NU1101: Unable to find package Aspire.Hosting.Bogus.DoesNotExist.");
                    return 1;
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var interactionService = (TestInteractionService)provider.GetRequiredService<IInteractionService>();
        Assert.Equal(Aspire.Cli.CliExitCodes.FailedToBuildArtifacts, exitCode);
        Assert.Contains(interactionService.DisplayedLines, line => line is (OutputLineStream.StdOut, "Determining projects to restore..."));
        Assert.Contains(interactionService.DisplayedLines, line => line is (OutputLineStream.StdErr, "error NU1101: Unable to find package Aspire.Hosting.Bogus.DoesNotExist."));
    }
}
