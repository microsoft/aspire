// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class SdkInstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RunCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a minimal project file so project detection succeeds
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsAspireHost>true</IsAspireHost>
                </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"), projectContent);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            // Use TestDotNetCliRunner to avoid real process execution
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task AddCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            options.InteractionServiceFactory = _ => new TestInteractionService();

            // Need to provide a project locator since AddCommand checks for project first
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("add");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task NewCommand_WhenSdkNotInstalled_OnlyShowsCliTemplates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // With no SDK, aspire-starter shouldn't be a valid subcommand
        var result = command.Parse("new aspire-starter");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        // aspire-starter is not registered when SDK is unavailable, so it's an invalid command
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task PublishCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a minimal project file so project detection succeeds
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsAspireHost>true</IsAspireHost>
                </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"), projectContent);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            // Use TestDotNetCliRunner to avoid real process execution
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("publish");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task DeployCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a minimal project file so project detection succeeds
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsAspireHost>true</IsAspireHost>
                </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"), projectContent);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            // Use TestDotNetCliRunner to avoid real process execution
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("deploy");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302") // SDK not installed
            };

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenSdkInstalled_ContinuesNormalExecution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (true, "9.0.302", "9.0.302") // SDK installed
            };
            // Make sure project locator doesn't find projects so it fails at the expected point
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        // Should fail at project location, not SDK check
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }
}