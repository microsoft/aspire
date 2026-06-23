// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class SdkInstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RunCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task AddCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("add");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task NewCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task PublishCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("publish");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task DeployCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("deploy");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task ExecCommand_WhenSdkNotInstalled_ReturnsCorrectExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.ExecCommandEnabled];
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => false // SDK not installed
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("exec");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        Assert.Equal(ExitCodeConstants.SdkNotInstalled, exitCode);
    }

    [Fact]
    public async Task RunCommand_WhenSdkInstalled_ContinuesNormalExecution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetRuntimeSelectorFactory = _ => new TestDotNetRuntimeSelector
            {
                InitializeAsyncCallback = _ => true // SDK installed
            };
            // Make sure project locator doesn't find projects so it fails at the expected point
            options.ProjectLocatorFactory = _ => new NoProjectFileProjectLocator();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        // Should fail at project location, not SDK check
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }
}
