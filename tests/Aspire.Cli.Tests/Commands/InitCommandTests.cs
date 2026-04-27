// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class InitCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Test.csproj")]
    [InlineData("Test.fsproj")]
    [InlineData("Test.vbproj")]
    public async Task InitCommand_WhenSolutionAndProjectInSameDirectory_ReturnsError(string projectFileName)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a solution file and a project file in the same directory
        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectFileName));
        File.WriteAllText(projectFile.FullName, "<Project />");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                // GetSolutionProjectsAsync should not be called because the check
                // happens before reading solution projects
                runner.GetSolutionProjectsAsyncCallback = (_, _, _) =>
                {
                    throw new InvalidOperationException("GetSolutionProjectsAsync should not be called when solution and project are in the same directory.");
                };
                return runner;
            };
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.FailedToCreateNewProject, exitCode);
    }

    [Fact]
    public async Task InitCommand_WhenSolutionDirectoryHasNoProjectFiles_Proceeds()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a solution file only (no project files in the same directory)
        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var getSolutionProjectsCalled = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetSolutionProjectsAsyncCallback = (_, _, _) =>
                {
                    getSolutionProjectsCalled = true;
                    // Return success with no projects - the test verifies the check passed
                    return (0, Array.Empty<FileInfo>());
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    // Create the expected directories so the code can find them
                    var appHostDir = Path.Combine(outputPath, "Test.AppHost");
                    var serviceDefaultsDir = Path.Combine(outputPath, "Test.ServiceDefaults");
                    Directory.CreateDirectory(appHostDir);
                    Directory.CreateDirectory(serviceDefaultsDir);
                    File.WriteAllText(Path.Combine(appHostDir, "Test.AppHost.csproj"), "<Project />");
                    File.WriteAllText(Path.Combine(serviceDefaultsDir, "Test.ServiceDefaults.csproj"), "<Project />");
                    return 0;
                };
                return runner;
            };
            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        // Assert - the command should have proceeded past the directory check and created projects
        Assert.True(getSolutionProjectsCalled, "GetSolutionProjectsAsync should have been called when no project files are in the solution directory.");
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost", "Test.AppHost.csproj")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.ServiceDefaults", "Test.ServiceDefaults.csproj")));
    }

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

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

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
                var interactionService = new TestInteractionService();
                return interactionService;
            };

            // Mock packaging service
            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command
        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

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
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetConfigFile, useCache, invocationOptions, cancellationToken) =>
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
            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act - Invoke init command (suppress agent init to isolate the prompt behavior being tested)
        var parseResult = initCommand.Parse("init --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.False(promptedForProjectName, "Should not have prompted for project name");
        Assert.False(promptedForOutputPath, "Should not have prompted for output path");
    }

    [Fact]
    public async Task InitCommand_WhenCSharpLanguageIsPromptedAndSaved_DoesNotFailDueToPrecedingConfigFileWrite_Regression15750()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/15750
        //
        // Bug: LanguageService.GetOrPromptForProjectAsync persists appHost.language to
        // aspire.config.json BEFORE CreateEmptyAppHostAsync invokes
        // dotnet new aspire-apphost-singlefile.  The dotnet new template also emits
        // aspire.config.json into the same working directory, so the pre-existing file
        // causes a collision and dotnet new fails.
        //
        // This test drives the interactive prompt path (no --language flag) so the
        // language-save happens, mirrors the collision by failing NewProjectAsync when
        // aspire.config.json already exists, and then asserts the command succeeds with
        // a final config containing both appHost.path and appHost.language.
        // Against the current buggy code the test will fail because the command returns
        // a non-zero exit code due to the collision.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Override the language service to mirror the real LanguageService behavior:
            // when GetOrPromptForProjectAsync is called without an explicit language id and
            // saveLanguageSelection is true, it persists the selection to aspire.config.json via
            // ConfigurationService before returning the C# project.
            options.LanguageServiceFactory = (sp) =>
            {
                var defaultCsharpProject = sp.GetRequiredService<DotNetAppHostProject>();
                var configurationService = sp.GetRequiredService<IConfigurationService>();
                return new TestLanguageService
                {
                    DefaultProject = defaultCsharpProject,
                    GetOrPromptForProjectSelectionAsyncCallback = (explicitLanguage, saveLanguageSelection, ct) =>
                    {
                        Assert.Null(explicitLanguage);
                        Assert.False(saveLanguageSelection);

                        return Task.FromResult(new AppHostProjectSelection(defaultCsharpProject, ShouldPersistSelection: true));
                    },
                    GetOrPromptForProjectAsyncCallback = async (explicitLanguage, saveLanguageSelection, ct) =>
                    {
                        if (string.IsNullOrWhiteSpace(explicitLanguage) && saveLanguageSelection)
                        {
                            // Reproduce the exact write that real LanguageService performs via
                            // ConfigurationService.SetConfigurationAsync("appHost.language", "csharp").
                            var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
                            await File.WriteAllTextAsync(configPath,
                                """{"appHost":{"language":"csharp"}}""", ct);
                        }

                        return defaultCsharpProject;
                    },
                    SetLanguageAsyncCallback = (project, isGlobal, ct) =>
                    {
                        Assert.Same(defaultCsharpProject, project);
                        Assert.False(isGlobal);

                        return configurationService.SetConfigurationAsync("appHost.language", project.LanguageId, isGlobal, ct);
                    }
                };
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                    (ExitCode: 0, TemplateVersion: "10.0.0");

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    // Simulate dotnet new aspire-apphost-singlefile running in the working
                    // directory (UseWorkingDirectory = true).  The real dotnet new tool fails
                    // with a non-zero exit code when aspire.config.json already exists there.
                    var outputDir = Path.GetFullPath(outputPath);
                    var configFilePath = Path.Combine(outputDir, "aspire.config.json");

                    if (File.Exists(configFilePath))
                    {
                        // Collision: aspire.config.json was written by the language save.
                        return 1;
                    }

                    // No collision: create the files that an older single-file template would produce.
                    File.WriteAllText(Path.Combine(outputDir, "apphost.cs"), "// apphost");
                    File.WriteAllText(configFilePath, """{"appHost":{"path":"apphost.cs"}}""");
                    return 0;
                };

                return runner;
            };

            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        // Act: run without --language to exercise the interactive prompt+save path.
        var parseResult = initCommand.Parse("init --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        // Assert: the command must succeed and the final aspire.config.json must contain
        // both the template's appHost.path entry and the language selection.
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        Assert.True(File.Exists(configPath), "aspire.config.json should exist after init");

        var configJson = await File.ReadAllTextAsync(configPath);
        Assert.Contains("apphost.cs", configJson);
        Assert.Contains("csharp", configJson);
    }

    [Fact]
    public async Task InitCommand_WhenLanguageIsExplicit_DoesNotPersistLanguageAgain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffolded = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.LanguageServiceFactory = (sp) =>
            {
                var projectFactory = sp.GetRequiredService<IAppHostProjectFactory>();
                var tsProject = projectFactory.GetProject(CreateTypeScriptLanguageInfo());

                return new TestLanguageService
                {
                    DefaultProject = tsProject,
                    GetOrPromptForProjectSelectionAsyncCallback = (explicitLanguage, saveLanguageSelection, ct) =>
                    {
                        Assert.Equal(KnownLanguageId.TypeScript, explicitLanguage);
                        Assert.False(saveLanguageSelection);

                        return Task.FromResult(new AppHostProjectSelection(tsProject, ShouldPersistSelection: false));
                    },
                    SetLanguageAsyncCallback = (_, _, _) => throw new InvalidOperationException("Explicit language selection should not be persisted again.")
                };
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffolded = true;
                return Task.FromResult(true);
            }
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse($"init --language {KnownLanguageId.TypeScript} --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scaffolded);
    }

    [Fact]
    public async Task InitCommand_WhenLanguageIsConfigured_DoesNotPersistLanguageAgain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffolded = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.LanguageServiceFactory = (sp) =>
            {
                var projectFactory = sp.GetRequiredService<IAppHostProjectFactory>();
                var tsProject = projectFactory.GetProject(CreateTypeScriptLanguageInfo());

                return new TestLanguageService
                {
                    DefaultProject = tsProject,
                    GetOrPromptForProjectSelectionAsyncCallback = (explicitLanguage, saveLanguageSelection, ct) =>
                    {
                        Assert.Null(explicitLanguage);
                        Assert.False(saveLanguageSelection);

                        return Task.FromResult(new AppHostProjectSelection(tsProject, ShouldPersistSelection: false));
                    },
                    SetLanguageAsyncCallback = (_, _, _) => throw new InvalidOperationException("Configured language selection should not be persisted again.")
                };
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffolded = true;
                return Task.FromResult(true);
            }
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scaffolded);
    }

    [Fact]
    public async Task InitCommand_WhenLanguageIsPrompted_PersistsLanguageAfterScaffoldingSucceeds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffolded = false;
        var persistedLanguage = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.LanguageServiceFactory = (sp) =>
            {
                var projectFactory = sp.GetRequiredService<IAppHostProjectFactory>();
                var tsProject = projectFactory.GetProject(CreateTypeScriptLanguageInfo());

                return new TestLanguageService
                {
                    DefaultProject = tsProject,
                    GetOrPromptForProjectSelectionAsyncCallback = (explicitLanguage, saveLanguageSelection, ct) =>
                    {
                        Assert.Null(explicitLanguage);
                        Assert.False(saveLanguageSelection);

                        return Task.FromResult(new AppHostProjectSelection(tsProject, ShouldPersistSelection: true));
                    },
                    SetLanguageAsyncCallback = (project, isGlobal, ct) =>
                    {
                        Assert.Same(tsProject, project);
                        Assert.False(isGlobal);

                        persistedLanguage = true;
                        return Task.CompletedTask;
                    }
                };
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffolded = true;
                return Task.FromResult(true);
            }
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scaffolded);
        Assert.True(persistedLanguage);
    }

    private static TestPackagingService CreatePackagingServiceWithTemplatePackages() => new()
    {
        GetChannelsAsyncCallback = _ =>
        {
            var cache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([
                        new Aspire.Shared.NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "10.0.0" }
                    ])
            };
            return Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)]);
        }
    };

    private static TestPackagingService CreatePackagingServiceWithChannelTracking(Action<string> onChannelUsed)
    {
        FakeNuGetPackageCache CreateTrackingCache(string channelName) => new()
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
            {
                onChannelUsed(channelName);
                return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([
                    new Aspire.Shared.NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "10.0.0" }
                ]);
            }
        };

        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Both, [], CreateTrackingCache("stable"));
                var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [], CreateTrackingCache("daily"));
                return Task.FromResult<IEnumerable<PackageChannel>>([stableChannel, dailyChannel]);
            }
        };
    }

    private static LanguageInfo CreateTypeScriptLanguageInfo() => new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "@aspire/app-host",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "typescript",
        AppHostFileName: "apphost.ts");

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

            options.PackagingServiceFactory = _ => CreatePackagingServiceWithChannelTracking((channelName) => channelNameUsed = channelName);
            
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
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init --channel stable");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
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
            options.PackagingServiceFactory = _ => CreatePackagingServiceWithChannelTracking(_ => { });
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init --channel invalid-channel");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Assert - should fail with non-zero exit code for invalid channel
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task InitCommand_WhenCSharpInitializationFails_DisplaysCreationErrorMessage()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a solution file only (no project files in the same directory)
        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetSolutionProjectsAsyncCallback = (_, _, _) =>
                {
                    return (0, Array.Empty<FileInfo>());
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 1; // Simulate failure for C# template
                };
                return runner;
            };
            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var expectedMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ProjectCouldNotBeCreated, executionContext.LogFilePath);

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(testInteractionService);
        Assert.Contains(expectedMessage, testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task InitCommand_WhenTypeScriptInitializationFails_DisplaysCreationErrorMessage()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.LanguageServiceFactory = (sp) =>
            {
                var projectFactory = sp.GetRequiredService<IAppHostProjectFactory>();
                var tsProject = projectFactory.GetProject(new LanguageInfo(
                    LanguageId: new LanguageId(KnownLanguageId.TypeScript),
                    DisplayName: "TypeScript (Node.js)",
                    PackageName: "@aspire/app-host",
                    DetectionPatterns: ["apphost.ts"],
                    CodeGenerator: "typescript",
                    AppHostFileName: "apphost.ts"));
                return new TestLanguageService { DefaultProject = tsProject };
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                return Task.FromResult(false); // Simulate failure for TypeScript scaffolding
            }
        });

        using var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var expectedMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ProjectCouldNotBeCreated, executionContext.LogFilePath);

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(testInteractionService);
        Assert.Contains(expectedMessage, testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task InitCommandNonInteractive_NoSuppressAgentInitOption_DefaultsToRunAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
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
                    File.WriteAllText(Path.Combine(outputPath, "apphost.cs"), "// Test apphost file");
                    return 0;
                };
                return runner;
            };

            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
    }

    [Fact]
    public async Task InitCommandNonInteractive_SuppressAgentInitTrue_SkipsAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
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
                    File.WriteAllText(Path.Combine(outputPath, "apphost.cs"), "// Test apphost file");
                    return 0;
                };
                return runner;
            };

            options.PackagingServiceFactory = _ => CreatePackagingServiceWithTemplatePackages();
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<InitCommand>();
        var result = command.Parse("init --suppress-agent-init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
    }
}
