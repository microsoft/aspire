// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class UpdateCommandSelfUpdateDisabledTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task UpdateCommand_SelfFlag_WhenSelfUpdateDisabled_ShowsDisabledMessageAndReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InstallationDetectorFactory = _ => new TestInstallationDetector
            {
                InstallationInfo = new InstallationInfo(
                    IsDotNetTool: false,
                    SelfUpdateDisabled: true,
                    UpdateInstructions: "brew upgrade --cask aspire")
            };

            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        var provider = services.BuildServiceProvider();
        var interactionService = provider.GetRequiredService<IInteractionService>() as TestInteractionService;

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Contains(interactionService!.DisplayedMessages,
            m => m.Message.Contains("Self-update is disabled"));
    }

    [Fact]
    public async Task UpdateCommand_PostProjectUpdate_WhenSelfUpdateDisabled_DoesNotPrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var confirmCallbackInvoked = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InstallationDetectorFactory = _ => new TestInstallationDetector
            {
                InstallationInfo = new InstallationInfo(
                    IsDotNetTool: false,
                    SelfUpdateDisabled: true,
                    UpdateInstructions: "winget upgrade Microsoft.Aspire")
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                ConfirmCallback = (prompt, defaultValue) =>
                {
                    confirmCallbackInvoked = true;
                    return false;
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (projectFile, channel, cancellationToken) =>
                {
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (cancellationToken) =>
                {
                    var stableChannel = PackageChannel.CreateExplicitChannel(
                        "stable",
                        PackageChannelQuality.Stable,
                        new[] { new PackageMapping("Aspire*", "https://api.nuget.org/v3/index.json") },
                        null!,
                        configureGlobalPackagesFolder: false,
                        cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/ga/daily");
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel });
                }
            };

            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier()
            {
                IsUpdateAvailableCallback = () => true
            };
        });

        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --apphost AppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.False(confirmCallbackInvoked, "Confirm prompt should NOT be shown when self-update is disabled");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_NoProjectFound_WhenSelfUpdateDisabled_ShowsInstructionsInsteadOfPrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var confirmCallbackInvoked = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InstallationDetectorFactory = _ => new TestInstallationDetector
            {
                InstallationInfo = new InstallationInfo(
                    IsDotNetTool: false,
                    SelfUpdateDisabled: true,
                    UpdateInstructions: "brew upgrade --cask aspire")
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound);
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                ConfirmCallback = (prompt, defaultValue) =>
                {
                    confirmCallbackInvoked = true;
                    return false;
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();
        });

        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.False(confirmCallbackInvoked, "Confirm prompt should NOT be shown when self-update is disabled");
    }
}