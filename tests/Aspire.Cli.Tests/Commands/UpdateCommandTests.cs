// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Runtime.InteropServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class UpdateCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task UpdateCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_WhenProjectOptionSpecified_PassesProjectFileToProjectLocator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        FileInfo? capturedProjectFile = null;
        var projectLocatorInvoked = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    projectLocatorInvoked = true;
                    capturedProjectFile = projectFile;
                    return Task.FromResult<FileInfo?>(projectFile);
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService();

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService();
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"update --apphost AppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(projectLocatorInvoked);
        Assert.NotNull(capturedProjectFile);
        Assert.Equal("AppHost.csproj", capturedProjectFile.Name);
    }

    [Fact]
    public void CleanupOldBackupFiles_DeletesFilesMatchingPattern()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetExePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.exe");
        var oldBackup1 = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.exe.old.1234567890");
        var oldBackup2 = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.exe.old.9876543210");
        var otherFile = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.exe.something");

        // Create test files
        File.WriteAllText(oldBackup1, "test");
        File.WriteAllText(oldBackup2, "test");
        File.WriteAllText(otherFile, "test");

        // Act
        FileDeleteHelper.TryCleanupOldItems(workspace.WorkspaceRoot.FullName, "aspire.exe");

        // Assert
        Assert.False(File.Exists(oldBackup1), "Old backup file should be deleted");
        Assert.False(File.Exists(oldBackup2), "Old backup file should be deleted");
        Assert.True(File.Exists(otherFile), "Other files should not be deleted");
    }

    [Fact]
    public void CleanupOldBackupFiles_HandlesInUseFilesGracefully()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldBackup = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.exe.old.1234567890");

        // Create and lock the backup file
        File.WriteAllText(oldBackup, "test");
        using var fileStream = new FileStream(oldBackup, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act & Assert - should not throw exception
        FileDeleteHelper.TryCleanupOldItems(workspace.WorkspaceRoot.FullName, "aspire.exe");

        // On Windows, locked files cannot be deleted, so the file should still exist
        // On Mac/Linux, locked files can be deleted, so the file may be deleted
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(File.Exists(oldBackup), "Locked file should still exist on Windows");
        }
        else
        {
            Assert.False(File.Exists(oldBackup), "Locked file should be deleted on Mac/Linux");
        }
    }

    [Fact]
    public void CleanupOldBackupFiles_HandlesNonExistentDirectory()
    {
        // Act & Assert - should not throw exception
        FileDeleteHelper.TryCleanupOldItems(Path.Combine("C:", "NonExistent"), "aspire.exe");
    }

    [Fact]
    public void CleanupOldBackupFiles_HandlesEmptyDirectory()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Act & Assert - should not throw exception
        FileDeleteHelper.TryCleanupOldItems(workspace.WorkspaceRoot.FullName, "aspire.exe");
    }

    [Fact]
    public async Task UpdateCommand_WhenNoProjectFound_PromptsForCliSelfUpdate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var confirmCallbackInvoked = false;
        string? confirmPrompt = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    // Simulate no project found by throwing ProjectLocatorException
                    throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound);
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                ConfirmCallback = (prompt, defaultValue) =>
                {
                    confirmCallbackInvoked = true;
                    confirmPrompt = prompt;
                    return false; // User says no
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.True(confirmCallbackInvoked, "Confirm prompt should have been shown");
        Assert.NotNull(confirmPrompt);
        Assert.Contains("Would you like to update the Aspire CLI", confirmPrompt);
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_WhenProjectUpdatedSuccessfully_AndChannelSupportsCliDownload_PromptsForCliUpdate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var confirmCallbackInvoked = false;
        string? confirmPrompt = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
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
                    confirmPrompt = prompt;
                    return false; // User says no
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            // Return a channel with CliDownloadBaseUrl to enable CLI update prompts
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

            // Configure update notifier to report that an update is available
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier()
            {
                IsUpdateAvailableCallback = () => true
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --apphost AppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.True(confirmCallbackInvoked, "Confirm prompt should have been shown after successful project update");
        Assert.NotNull(confirmPrompt);
        Assert.Contains("An update is available for the Aspire CLI", confirmPrompt);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_WithoutAutoConfirmOption_UsesFalseConfirmationDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var updateProjectInvoked = false;
        var confirmBindingResolved = false;
        var confirmBindingValue = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    updateProjectInvoked = true;
                    (confirmBindingResolved, confirmBindingValue) = context.ConfirmBinding.Resolve();
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --apphost AppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(updateProjectInvoked);
        Assert.False(confirmBindingResolved);
        Assert.False(confirmBindingValue);
    }

    [Fact]
    public async Task UpdateCommand_WithYesOption_ResolvesConfirmationFromCli()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var updateProjectInvoked = false;
        var confirmBindingResolved = false;
        var confirmBindingValue = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    updateProjectInvoked = true;
                    (confirmBindingResolved, confirmBindingValue) = context.ConfirmBinding.Resolve();
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --apphost AppHost.csproj --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(updateProjectInvoked);
        Assert.True(confirmBindingResolved);
        Assert.True(confirmBindingValue);
    }

    [Fact]
    public async Task UpdateCommand_WhenChannelHasNoCliDownloadUrl_DoesNotPromptForCliUpdate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var confirmCallbackInvoked = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
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
                    return false; // User says no
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            // Return a channel without CliDownloadBaseUrl (like PR channels)
            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (cancellationToken) =>
                {
                    var prChannel = PackageChannel.CreateExplicitChannel(
                        "pr-12658",
                        PackageChannelQuality.Prerelease,
                        new[] { new PackageMapping("Aspire*", "/path/to/pr/hive") },
                        null!,
                        configureGlobalPackagesFolder: false,
                        cliDownloadBaseUrl: null); // No CLI download URL for PR channels
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { prChannel });
                }
            };

            // Configure update notifier to report that an update is available
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier()
            {
                IsUpdateAvailableCallback = () => true
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --apphost AppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.False(confirmCallbackInvoked, "Confirm prompt should NOT have been shown for channels without CLI download support");
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WithChannelOption_DoesNotPromptForChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        string? capturedChannel = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return "stable";
                }
            };

            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (channel, ct) =>
                {
                    capturedChannel = channel;
                    // Create a fake archive file
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self --channel daily");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.False(promptForSelectionInvoked, "Channel prompt should not be shown when --channel is provided");
        Assert.Equal("daily", capturedChannel);
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WithQualityOption_DoesNotPromptForQuality()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        string? capturedQuality = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return "stable";
                }
            };

            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (quality, ct) =>
                {
                    capturedQuality = quality;
                    // Create a fake archive file
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self --quality daily");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.False(promptForSelectionInvoked, "Quality prompt should not be shown when --quality is provided");
        Assert.Equal("daily", capturedQuality);
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WithChannelOption_TracksChannelParameter()
    {
        // This test verifies that the channel parameter flows through the self-update command.
        // Full integration testing of SetConfigurationAsync would require creating a valid
        // tar.gz archive with a working CLI executable, which is complex for a unit test.
        // The test verifies the channel value is properly captured and would be passed
        // to configuration service if the extraction succeeds.
        
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        string? capturedChannel = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (channel, ct) =>
                {
                    capturedChannel = channel;
                    // Create a fake archive file - extraction will fail but channel is captured
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self --channel daily");

        // Note: exitCode will be non-zero because extraction fails, but that's okay for this test
        await result.InvokeAsync().DefaultTimeout();

        // Assert - verify the channel parameter was correctly passed through
        Assert.Equal("daily", capturedChannel);
    }

    [Fact]
    public async Task UpdateCommand_ProjectUpdate_WithChannelOption_DoesNotPromptForChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        PackageChannel? capturedChannel = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return choices.Cast<PackageChannel>().First();
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    capturedChannel = context.Channel;
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    // Create test channels matching the expected names
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    var dailyChannel = new PackageChannel("daily", PackageChannelQuality.Prerelease, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel, dailyChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --channel daily");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.False(promptForSelectionInvoked, "Channel prompt should not be shown when --channel is provided");
        Assert.NotNull(capturedChannel);
        Assert.Equal("daily", capturedChannel.Name);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_ProjectUpdate_WithQualityOption_DoesNotPromptForChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        PackageChannel? capturedChannel = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return choices.Cast<PackageChannel>().First();
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    capturedChannel = context.Channel;
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    // Create test channels matching the expected names
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    var dailyChannel = new PackageChannel("daily", PackageChannelQuality.Prerelease, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel, dailyChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --quality daily");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.False(promptForSelectionInvoked, "Channel prompt should not be shown when --quality is provided");
        Assert.NotNull(capturedChannel);
        Assert.Equal("daily", capturedChannel.Name);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_ProjectUpdate_WithInvalidQuality_DisplaysError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        TestInteractionService? testInteractionService = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ =>
            {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater();

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    // Create test channels matching the expected names
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    var dailyChannel = new PackageChannel("daily", PackageChannelQuality.Prerelease, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel, dailyChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --quality invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.NotNull(testInteractionService);
        Assert.NotEmpty(testInteractionService.DisplayedErrors);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Contains("invalid", errorMessage);
        Assert.Contains("stable", errorMessage);
        Assert.Contains("daily", errorMessage);
        Assert.Equal(ExitCodeConstants.FailedToUpgradeProject, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_ProjectUpdate_ChannelTakesPrecedenceOverQuality()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        PackageChannel? capturedChannel = null;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return choices.Cast<PackageChannel>().First();
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    capturedChannel = context.Channel;
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    var dailyChannel = new PackageChannel("daily", PackageChannelQuality.Prerelease, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel, dailyChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act - specify both --channel and --quality, --channel should win
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --channel stable --quality daily");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert - should use "stable" from --channel, not "daily" from --quality
        Assert.False(promptForSelectionInvoked, "Channel prompt should not be shown");
        Assert.NotNull(capturedChannel);
        Assert.Equal("stable", capturedChannel.Name);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_ProjectUpdate_WhenCancelled_DisplaysCancellationMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a hive directory so the channel prompt is shown
        var hivesDir = workspace.CreateDirectory(".aspire").CreateSubdirectory("hives");
        hivesDir.CreateSubdirectory("pr-12345");

        var cancellationMessageDisplayed = false;
        
        var wrappedService = new CancellationTrackingInteractionService(new TestInteractionService()
        {
            PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
            {
                // Simulate user pressing Ctrl+C during selection prompt
                throw new OperationCanceledException();
            }
        });
        wrappedService.OnCancellationMessageDisplayed = () => cancellationMessageDisplayed = true;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => wrappedService;

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.True(cancellationMessageDisplayed, "Cancellation message should have been displayed");
        Assert.Equal(ExitCodeConstants.FailedToUpgradeProject, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_WithoutHives_UsesImplicitChannelWithoutPrompting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        var updatedWithChannel = string.Empty;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return choices.Cast<object>().First();
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    updatedWithChannel = context.Channel.Name;
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    var fakeCache = new FakeNuGetPackageCache();
                    var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { implicitChannel });
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act - without hives, should automatically use implicit channel
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptForSelectionInvoked, "Channel selection prompt should not be shown when there are no hives");
        Assert.Equal("default", updatedWithChannel); // Implicit channel is named "default"
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WhenCancelled_DisplaysCancellationMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a hive directory so the channel prompt is shown
        var hivesDir = workspace.CreateDirectory(".aspire").CreateSubdirectory("hives");
        hivesDir.CreateSubdirectory("pr-12345");

        var cancellationMessageDisplayed = false;
        
        var wrappedService = new CancellationTrackingInteractionService(new TestInteractionService()
        {
            PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
            {
                // Simulate user pressing Ctrl+C during channel selection prompt
                throw new OperationCanceledException();
            }
        });
        wrappedService.OnCancellationMessageDisplayed = () => cancellationMessageDisplayed = true;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => wrappedService;

            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot);
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.True(cancellationMessageDisplayed, "Cancellation message should have been displayed");
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WhenStagingFeatureFlagDisabled_DoesNotShowStagingChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        IEnumerable? capturedChoices = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    capturedChoices = choices;
                    return PackageChannelNames.Stable;
                }
            };

            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (channel, ct) =>
                {
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self");

        await result.InvokeAsync().DefaultTimeout();

        Assert.NotNull(capturedChoices);
        var channelList = capturedChoices.Cast<string>().ToList();
        Assert.DoesNotContain(PackageChannelNames.Staging, channelList);
        Assert.Contains(PackageChannelNames.Stable, channelList);
        Assert.Contains(PackageChannelNames.Daily, channelList);
    }

    [Fact]
    public async Task UpdateCommand_SelfUpdate_WhenStagingFeatureFlagEnabled_ShowsStagingChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        IEnumerable? capturedChoices = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.StagingChannelEnabled];

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    capturedChoices = choices;
                    return PackageChannelNames.Stable;
                }
            };

            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (channel, ct) =>
                {
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --self");

        await result.InvokeAsync().DefaultTimeout();

        Assert.NotNull(capturedChoices);
        var channelList = capturedChoices.Cast<string>().ToList();
        Assert.Contains(PackageChannelNames.Staging, channelList);
        Assert.Contains(PackageChannelNames.Stable, channelList);
        Assert.Contains(PackageChannelNames.Daily, channelList);
    }

    [Fact]
    public async Task UpdateCommand_SelfOption_IsAvailableAndParseable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliDownloaderFactory = _ => new TestCliDownloader(workspace.WorkspaceRoot)
            {
                DownloadLatestCliAsyncCallback = (channel, ct) =>
                {
                    // Create a fake archive file
                    var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-cli.tar.gz");
                    File.WriteAllText(archivePath, "fake archive");
                    return Task.FromResult(archivePath);
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        
        // Act - Parse command with --self option
        var result = command.Parse("update --self --channel stable");
        
        // Assert - Command should parse successfully without errors
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task UpdateCommand_NonInteractive_WithYesAndChannel_SucceedsWithoutPrompting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var promptForSelectionInvoked = false;
        var confirmCallbackInvoked = false;
        PackageChannel? capturedChannel = null;
        UpdatePackagesContext? capturedContext = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (projectFile, _, _) =>
                {
                    return Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")));
                }
            };

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (prompt, choices, formatter, ct) =>
                {
                    promptForSelectionInvoked = true;
                    return choices.Cast<object>().First();
                },
                ConfirmCallback = (prompt, defaultValue) =>
                {
                    confirmCallbackInvoked = true;
                    return true;
                }
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (context, cancellationToken) =>
                {
                    capturedContext = context;
                    capturedChannel = context.Channel;
                    return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (ct) =>
                {
                    var stableChannel = new PackageChannel("stable", PackageChannelQuality.Stable, null, null!);
                    var dailyChannel = new PackageChannel("daily", PackageChannelQuality.Prerelease, null, null!);
                    return Task.FromResult<IEnumerable<PackageChannel>>([stableChannel, dailyChannel]);
                }
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --yes --channel stable --non-interactive");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptForSelectionInvoked, "No selection prompt should be shown in non-interactive mode with --channel");
        Assert.False(confirmCallbackInvoked, "No confirm prompt should be shown in non-interactive mode with --yes");
        Assert.NotNull(capturedChannel);
        Assert.Equal("stable", capturedChannel.Name);
        Assert.NotNull(capturedContext);
    }
}

// Helper class to track DisplayCancellationMessage calls
internal sealed class CancellationTrackingInteractionService : IInteractionService
{
    private readonly IInteractionService _innerService;

    public ConsoleOutput Console
    {
        get => _innerService.Console;
        set => _innerService.Console = value;
    }

    public Action? OnCancellationMessageDisplayed { get; set; }

    public CancellationTrackingInteractionService(IInteractionService innerService)
    {
        _innerService = innerService;
    }

    public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false) => _innerService.ShowStatusAsync(statusText, action, emoji, allowMarkup);
    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false) => _innerService.ShowStatus(statusText, action, emoji, allowMarkup);
    public Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) 
        => _innerService.PromptForStringAsync(promptText, validator, isSecret, required, binding, cancellationToken);
    public Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
        => _innerService.PromptForFilePathAsync(promptText, validator, directory, required, binding, cancellationToken);
    public Task<bool> PromptConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default) 
        => _innerService.PromptConfirmAsync(promptText, binding, cancellationToken);
    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull 
        => _innerService.PromptForSelectionAsync(promptText, choices, choiceFormatter, binding, cancellationToken);
    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull 
        => _innerService.PromptForSelectionsAsync(promptText, choices, choiceFormatter, preSelected, optional, binding, cancellationToken);
    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) 
        => _innerService.DisplayIncompatibleVersionError(ex, appHostHostingVersion);
    public void DisplayError(string errorMessage) => _innerService.DisplayError(errorMessage);
    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false) => _innerService.DisplayMessage(emoji, message, allowMarkup);
    public void DisplayPlainText(string text) => _innerService.DisplayPlainText(text);
    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null) => _innerService.DisplayRawText(text, consoleOverride);
    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null) => _innerService.DisplayMarkdown(markdown, consoleOverride);
    public void DisplayMarkupLine(string markup) => _innerService.DisplayMarkupLine(markup);
    public void DisplaySuccess(string message, bool allowMarkup = false) => _innerService.DisplaySuccess(message, allowMarkup);
    public void DisplaySubtleMessage(string message, bool allowMarkup = false) => _innerService.DisplaySubtleMessage(message, allowMarkup);
    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines) => _innerService.DisplayLines(lines);
    public void DisplayCancellationMessage() 
    {
        OnCancellationMessageDisplayed?.Invoke();
        _innerService.DisplayCancellationMessage();
    }
    public void DisplayEmptyLine() => _innerService.DisplayEmptyLine();
    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null) 
        => _innerService.DisplayVersionUpdateNotification(newerVersion, updateCommand);
    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false) 
        => _innerService.WriteConsoleLog(message, lineNumber, type, isErrorMessage);
    public void DisplayRenderable(IRenderable renderable) => _innerService.DisplayRenderable(renderable);
    public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback) => _innerService.DisplayLiveAsync(initialRenderable, callback);
}

// Test implementation of IProjectUpdater
internal sealed class TestProjectUpdater : IProjectUpdater
{
    public Func<UpdatePackagesContext, CancellationToken, Task<ProjectUpdateResult>>? UpdateProjectAsyncCallback { get; set; }

    public Task<ProjectUpdateResult> UpdateProjectAsync(UpdatePackagesContext context, CancellationToken cancellationToken = default)
    {
        if (UpdateProjectAsyncCallback != null)
        {
            return UpdateProjectAsyncCallback(context, cancellationToken);
        }

        // Default behavior
        return Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false });
    }
}
