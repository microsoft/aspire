// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class InitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void InitContext_RequiredAppHostFramework_ReturnsHighestTfm()
    {
        // Arrange
        var initContext = new InitContext();

        // Act & Assert - No projects selected returns default
        Assert.Equal("net9.0", initContext.RequiredAppHostFramework);

        // Set up projects with different TFMs
        initContext.ExecutableProjectsToAddToAppHost = new List<ExecutableProjectInfo>
        {
            new() { ProjectFile = new FileInfo("/test/project1.csproj"), TargetFramework = "net8.0" },
            new() { ProjectFile = new FileInfo("/test/project2.csproj"), TargetFramework = "net9.0" },
            new() { ProjectFile = new FileInfo("/test/project3.csproj"), TargetFramework = "net10.0" }
        };

        // Act
        var result = initContext.RequiredAppHostFramework;

        // Assert
        Assert.Equal("net10.0", result);

        // Test with only lower versions
        initContext.ExecutableProjectsToAddToAppHost = new List<ExecutableProjectInfo>
        {
            new() { ProjectFile = new FileInfo("/test/project1.csproj"), TargetFramework = "net8.0" },
            new() { ProjectFile = new FileInfo("/test/project2.csproj"), TargetFramework = "net9.0" }
        };

        result = initContext.RequiredAppHostFramework;
        Assert.Equal("net9.0", result);

        // Test with only net8.0
        initContext.ExecutableProjectsToAddToAppHost = new List<ExecutableProjectInfo>
        {
            new() { ProjectFile = new FileInfo("/test/project1.csproj"), TargetFramework = "net8.0" }
        };

        result = initContext.RequiredAppHostFramework;
        Assert.Equal("net8.0", result);
    }

    [Fact]
    public async Task InitCommand_WhenGetSolutionProjectsFails_SetsOutputCollectorAndCallsCallbacks()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a solution file to trigger InitializeExistingSolutionAsync path
        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        const string testErrorMessage = "Test error from dotnet sln list";
        var standardOutputCallbackInvoked = false;
        var standardErrorCallbackInvoked = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Mock the runner to return an error when GetSolutionProjectsAsync is called
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                runner.GetSolutionProjectsAsyncCallback = (solutionFile, invocationOptions, cancellationToken) =>
                {
                    // Verify that the OutputCollector callbacks are wired up
                    Assert.NotNull(invocationOptions.StandardOutputCallback);
                    Assert.NotNull(invocationOptions.StandardErrorCallback);

                    // Simulate calling the callbacks to verify they work
                    invocationOptions.StandardOutputCallback?.Invoke("Some output");
                    standardOutputCallbackInvoked = true;

                    invocationOptions.StandardErrorCallback?.Invoke(testErrorMessage);
                    standardErrorCallbackInvoked = true;

                    // Return a non-zero exit code to trigger the error path
                    return (1, Array.Empty<FileInfo>());
                };

                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);

        // Assert
        Assert.Equal(1, exitCode); // Should return the error exit code
        Assert.True(standardOutputCallbackInvoked, "StandardOutputCallback should have been invoked");
        Assert.True(standardErrorCallbackInvoked, "StandardErrorCallback should have been invoked");
    }

    [Fact]
    public async Task InitCommand_WhenNewProjectFails_SetsOutputCollectorAndCallsCallbacks()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a solution file to trigger InitializeExistingSolutionAsync path
        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        const string testErrorMessage = "Test error from dotnet new";
        var standardOutputCallbackInvoked = false;
        var standardErrorCallbackInvoked = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Mock the runner
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                runner.GetSolutionProjectsAsyncCallback = (solutionFile, invocationOptions, cancellationToken) =>
                {
                    return (0, Array.Empty<FileInfo>());
                };

                runner.GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, invocationOptions, cancellationToken) =>
                {
                    return (0, null);
                };

                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, cancellationToken) =>
                {
                    return (0, "10.0.0");
                };

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, cancellationToken) =>
                {
                    // Verify that the OutputCollector callbacks are wired up
                    Assert.NotNull(invocationOptions.StandardOutputCallback);
                    Assert.NotNull(invocationOptions.StandardErrorCallback);

                    // Simulate calling the callbacks to verify they work
                    invocationOptions.StandardOutputCallback?.Invoke("Some output");
                    standardOutputCallbackInvoked = true;

                    invocationOptions.StandardErrorCallback?.Invoke(testErrorMessage);
                    standardErrorCallbackInvoked = true;

                    // Return a non-zero exit code to trigger the error path
                    return 1;
                };

                return runner;
            };

            options.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestConsoleInteractionService();
                return interactionService;
            };

            // Mock packaging service
            options.PackagingServiceFactory = (sp) =>
            {
                return new TestPackagingService();
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);

        // Assert
        Assert.Equal(1, exitCode); // Should return the error exit code
        Assert.True(standardOutputCallbackInvoked, "StandardOutputCallback should have been invoked");
        Assert.True(standardErrorCallbackInvoked, "StandardErrorCallback should have been invoked");
    }

    [Fact]
    public async Task InitCommand_WithSingleFileAppHost_DoesNotPromptForProjectNameOrOutputPath()
    {
        // Arrange
        var promptedForProjectName = false;
        var promptedForOutputPath = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Set up prompter to track if prompts are called
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (defaultName) =>
                {
                    promptedForProjectName = true;
                    throw new InvalidOperationException("PromptForProjectName should not be called for init command with single-file AppHost");
                };

                prompter.PromptForOutputPathCallback = (path) =>
                {
                    promptedForOutputPath = true;
                    throw new InvalidOperationException("PromptForOutputPath should not be called for init command with single-file AppHost");
                };

                // PromptForTemplatesVersion is expected to be called
                prompter.PromptForTemplatesVersionCallback = (packages) => packages.First();

                return prompter;
            };

            // Mock the runner to avoid actual template installation and project creation
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                // Mock template installation
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, cancellationToken) =>
                {
                    return (ExitCode: 0, TemplateVersion: "10.0.0");
                };

                // Mock project creation
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, cancellationToken) =>
                {
                    // Verify the expected values are being used
                    Assert.Equal(workspace.WorkspaceRoot.Name, projectName);
                    Assert.Equal(workspace.WorkspaceRoot.FullName, Path.GetFullPath(outputPath));

                    // Create a minimal file to simulate successful template creation
                    var appHostFile = Path.Combine(outputPath, "apphost.cs");
                    File.WriteAllText(appHostFile, "// Test apphost file");

                    return 0;
                };

                // Mock package search for template version selection
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetConfigFile, useCache, invocationOptions, cancellationToken) =>
                {
                    var package = new Aspire.Shared.NuGetPackageCli
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "10.0.0"
                    };

                    return (0, new[] { package });
                };

                return runner;
            };

            // Mock packaging service to return fake channels
            options.PackagingServiceFactory = (sp) =>
            {
                return new TestPackagingService();
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.False(promptedForProjectName, "Should not have prompted for project name");
        Assert.False(promptedForOutputPath, "Should not have prompted for output path");
    }

    // Test implementation of INewCommandPrompter
    private sealed class TestNewCommandPrompter(IInteractionService interactionService) : NewCommandPrompter(interactionService)
    {
        public Func<IEnumerable<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)>, (Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)>? PromptForTemplatesVersionCallback { get; set; }
        public Func<string, string>? PromptForProjectNameCallback { get; set; }
        public Func<string, string>? PromptForOutputPathCallback { get; set; }

        public override Task<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> candidatePackages, CancellationToken cancellationToken)
        {
            return PromptForTemplatesVersionCallback switch
            {
                { } callback => Task.FromResult(callback(candidatePackages)),
                _ => Task.FromResult(candidatePackages.First())
            };
        }

        public override Task<string> PromptForProjectNameAsync(string defaultName, CancellationToken cancellationToken)
        {
            return PromptForProjectNameCallback switch
            {
                { } callback => Task.FromResult(callback(defaultName)),
                _ => Task.FromResult(defaultName)
            };
        }

        public override Task<string> PromptForOutputPath(string defaultPath, CancellationToken cancellationToken)
        {
            return PromptForOutputPathCallback switch
            {
                { } callback => Task.FromResult(callback(defaultPath)),
                _ => Task.FromResult(defaultPath)
            };
        }
    }

    // Test implementation of IPackagingService
    private sealed class TestPackagingService : IPackagingService
    {
        public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            // Return a fake channel with the implicit type (meaning use default NuGet sources)
            var testChannel = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());
            return Task.FromResult<IEnumerable<PackageChannel>>(new[] { testChannel });
        }
    }

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            var package = new Aspire.Shared.NuGetPackageCli
            {
                Id = "Aspire.ProjectTemplates",
                Source = "nuget",
                Version = "10.0.0"
            };
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(new[] { package });
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }
    }

    [Fact]
    public async Task InitCommandWithChannelOptionUsesSpecifiedChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        string? channelNameUsed = null;
        bool promptedForVersion = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                
                prompter.PromptForTemplatesVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not prompt for version when --channel is specified");
                };
                
                return prompter;
            };

            options.PackagingServiceFactory = (sp) =>
            {
                return new TestPackagingServiceWithChannelTracking((channelName) => channelNameUsed = channelName);
            };
            
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    var appHostFile = Path.Combine(outputPath, "apphost.cs");
                    File.WriteAllText(appHostFile, "// Test apphost file");
                    return 0;
                };
                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init --channel stable");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        
        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("stable", channelNameUsed);
        Assert.False(promptedForVersion);
    }

    [Fact]
    public async Task InitCommandWithInvalidChannelShowsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagingServiceFactory = (sp) =>
            {
                return new TestPackagingServiceWithChannelTracking(_ => { });
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init --channel invalid-channel");

        var exitCode = await result.InvokeAsync().WaitAsync(CliTestConstants.DefaultTimeout);
        
        // Assert - should fail with non-zero exit code for invalid channel
        Assert.NotEqual(0, exitCode);
    }

    private sealed class TestPackagingServiceWithChannelTracking(Action<string> onChannelUsed) : IPackagingService
    {
        public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            var stableCache = new FakeNuGetPackageCacheWithTracking("stable", onChannelUsed);
            var dailyCache = new FakeNuGetPackageCacheWithTracking("daily", onChannelUsed);
            
            var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Both, [], stableCache);
            var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [], dailyCache);
            
            return Task.FromResult<IEnumerable<PackageChannel>>(new[] { stableChannel, dailyChannel });
        }
    }

    private sealed class FakeNuGetPackageCacheWithTracking(string channelName, Action<string> onChannelUsed) : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            onChannelUsed(channelName);
            var package = new Aspire.Shared.NuGetPackageCli
            {
                Id = "Aspire.ProjectTemplates",
                Source = "nuget",
                Version = "10.0.0"
            };
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(new[] { package });
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(Array.Empty<Aspire.Shared.NuGetPackageCli>());
        }
    }
}
