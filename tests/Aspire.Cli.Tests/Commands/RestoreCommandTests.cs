// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
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

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RestoreAsyncCallback = (projectFilePath, _, _) =>
                {
                    restoreCalled = true;
                    capturedProjectFilePath = projectFilePath.FullName;
                    return Aspire.Cli.ExitCodeConstants.Success;
                }
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.ExitCodeConstants.Success, exitCode);
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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ => (false, null, "9.0.302")
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                RestoreAsyncCallback = (_, _, _) =>
                {
                    restoreCalled = true;
                    return Aspire.Cli.ExitCodeConstants.Success;
                }
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"restore --apphost {appHostFile.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.ExitCodeConstants.SdkNotInstalled, exitCode);
        Assert.False(restoreCalled);
    }
}
