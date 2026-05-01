// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Utils;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using RootCommand = Aspire.Cli.Commands.RootCommand;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Commands;

public class NewCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NewCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public void NewCommandWithPolyglotEnabled_ExposesTemplateSubcommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotGo, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotPython, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotRust, true);
                return features;
            };

        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        Assert.NotEmpty(command.Subcommands);
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.CSharpEmptyAppHost && subcommand.Description == "Empty AppHost (Choose language...)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.TypeScriptEmptyAppHost && subcommand.Description == "Empty (TypeScript AppHost)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.PythonEmptyAppHost && subcommand.Description == "Empty (Python AppHost)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.JavaEmptyAppHost && subcommand.Description == "Empty (Java AppHost)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.GoEmptyAppHost && subcommand.Description == "Empty (Go AppHost)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.RustEmptyAppHost && subcommand.Description == "Empty (Rust AppHost)");
    }

    [Fact]
    public void NewCommandWithPolyglotDisabled_ExposesTemplateSubcommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        Assert.NotEmpty(command.Subcommands);
        Assert.DoesNotContain(command.Options, option => option.Aliases.Contains("--language", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NewCommandInteractiveFlowSmokeTest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    // Quarantined due to flakiness. See linked issue for details.
    public async Task NewCommandDerivesOutputPathFromProjectNameForStarterTemplate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (defaultName) =>
                {
                    return "CustomName";
                };

                prompter.PromptForOutputPathCallback = (path) =>
                {
                    Assert.Equal("./CustomName", path);
                    return path;
                };

                return prompter;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForProjectNameIfSpecifiedOnCommandLine()
    {
        var promptedForName = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (defaultName) =>
                {
                    promptedForName = true;
                    throw new InvalidOperationException("This should not be called");
                };

                return prompter;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptedForName);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForOutputPathIfSpecifiedOnCommandLine()
    {
        bool promptedForPath = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForOutputPathCallback = (path) =>
                {
                    promptedForPath = true;
                    throw new InvalidOperationException("This should not be called");
                };

                return prompter;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --output notsrc --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptedForPath);
    }

    [Fact]
    public async Task NewCommandWithChannelOptionUsesSpecifiedChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        string? channelNameUsed = null;
        bool promptedForVersion = false;

        var services = CreateServiceCollection(workspace, options =>
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
                var packagingService = new TestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var stableCache = new FakeNuGetPackageCache();
                    stableCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        channelNameUsed = "stable";
                        var package = new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.2.0" };
                        return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                    };
                    
                    var dailyCache = new FakeNuGetPackageCache();
                    dailyCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        channelNameUsed = "daily";
                        var package = new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "10.0.0-dev" };
                        return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                    };
                    
                    var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Both, [], stableCache);
                    var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [], dailyCache);
                    
                    return Task.FromResult<IEnumerable<PackageChannel>>([stableChannel, dailyChannel]);
                };
                
                return packagingService;
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
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --channel stable --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Assert
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("stable", channelNameUsed); // Verify the stable channel was used
        Assert.False(promptedForVersion); // Should not prompt when --channel is specified
    }

    [Fact]
    public async Task NewCommandWithChannelOptionAutoSelectsHighestVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        string? selectedVersion = null;
        bool promptedForVersion = false;

        var services = CreateServiceCollection(workspace, options =>
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
                var packagingService = new TestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var fakeCache = new FakeNuGetPackageCache();
                    fakeCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        // Return multiple versions to test auto-selection of highest
                        var packages = new[]
                        {
                            new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.0.0" },
                            new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.2.0" },
                            new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.1.0" },
                        };
                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages);
                    };
                    
                    var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Both, [], fakeCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>([stableChannel]);
                };
                
                return packagingService;
            };
            
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    selectedVersion = version;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 0; // Success
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --channel stable --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Assert
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("9.2.0", selectedVersion); // Should auto-select highest version (9.2.0)
        Assert.False(promptedForVersion); // Should not prompt when --channel is specified
    }

    [Fact]
    public async Task NewCommandWithPrChannelPrefersCurrentCliVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        string? selectedVersion = null;
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
                    throw new InvalidOperationException("Should not prompt for version when a PR channel contains the current CLI version.");
                };

                return prompter;
            };

            options.PackagingServiceFactory = (sp) =>
            {
                var packagingService = new TestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var fakeCache = new FakeNuGetPackageCache();
                    fakeCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        var packages = new[]
                        {
                            new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "pr-hive", Version = cliVersion },
                            new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "pr-hive", Version = "99.0.0" },
                        };

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages);
                    };

                    var prChannel = PackageChannel.CreateExplicitChannel("pr-12345", PackageChannelQuality.Both, [], fakeCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>([prChannel]);
                };

                return packagingService;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    selectedVersion = version;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --channel pr-12345 --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal(cliVersion, selectedVersion);
        Assert.False(promptedForVersion);
    }

    [Fact]
    // Quarantined due to flakiness. See linked issue for details.
    public async Task NewCommandDoesNotPromptForTemplateIfSpecifiedOnCommandLine()
    {
        bool promptedForTemplate = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForTemplateCallback = (path) =>
                {
                    promptedForTemplate = true;
                    throw new InvalidOperationException("This should not be called");
                };

                return prompter;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptedForTemplate);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForTemplateVersionIfSpecifiedOnCommandLine()
    {
        bool promptedForTemplateVersion = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForTemplatesVersionCallback = (packages) =>
                {
                    promptedForTemplateVersion = true;
                    throw new InvalidOperationException("This should not be called");
                };

                return prompter;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(promptedForTemplateVersion);
    }

    [Fact]
    public async Task NewCommand_EmptyPackageList_DisplaysErrorMessage()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options => {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = (sp) => {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.DotNetCliRunnerFactory = (sp) => {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) => {
                    return (0, Array.Empty<NuGetPackage>());
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToCreateNewProject, exitCode);
        Assert.NotNull(testInteractionService);
        Assert.Contains(testInteractionService.DisplayedErrors, e => e.Contains(TemplatingStrings.NoTemplateVersionsFound));
    }

    [Fact]
    public async Task NewCommand_WhenCertificateServiceThrows_ReturnsNonZeroExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) => {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                return prompter;
            };
            options.CertificateServiceFactory = _ => new ThrowingCertificateService();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, options, cancellationToken) =>
                {
                    return (0, version); // Success, return the template version
                };

                runner.NewProjectAsyncCallback = (templateName, name, outputPath, options, cancellationToken) =>
                {
                    return 0; // Success
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToTrustCertificates, exitCode);
    }

    [Fact]
    public async Task NewCommandWithExitCode73ShowsUserFriendlyError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestNewCommandPrompter(interactionService);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, options, cancellationToken) =>
                {
                    return (0, version); // Success, return the template version
                };

                runner.NewProjectAsyncCallback = (templateName, name, outputPath, options, cancellationToken) =>
                {
                    return 73; // Simulate exit code 73 (directory already contains files)
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToCreateNewProject, exitCode);
    }

    private IServiceCollection CreateServiceCollection(
        TemporaryWorkspace workspace,
        Action<CliServiceCollectionTestOptions>? configure = null)
    {
        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => CreateTestRunnerWithStandardPackages();
            configure?.Invoke(options);
        });
    }

    private static TestDotNetCliRunner CreateTestRunnerWithStandardPackages()
    {
        var runner = new TestDotNetCliRunner();
        runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
        {
            var package = new NuGetPackage()
            {
                Id = "Aspire.ProjectTemplates",
                Source = "nuget",
                Version = "9.2.0"
            };

            return (0, new NuGetPackage[] { package });
        };
        return runner;
    }

    private sealed class ThrowingCertificateService : ICertificateService
    {
        public Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
        {
            throw new CertificateServiceException("Failed to trust certificates");
        }
    }

    [Fact]
    public async Task NewCommandPromptsForTemplateVersionBeforeTemplateOptions()
    {
        var operationOrder = new List<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForTemplatesVersionCallback = (packages) =>
                {
                    operationOrder.Add("TemplateVersion");
                    return packages.First();
                };

                return prompter;
            };

            options.InteractionServiceFactory = (sp) =>
            {
                var testInteractionService = new TestInteractionService();
                testInteractionService.PromptForSelectionCallback = (promptText, choices, formatter, ct) =>
                {
                    // Track template option prompts
                    if (promptText?.Contains("Redis") == true ||
                        promptText?.Contains("test framework") == true ||
                        promptText?.Contains("Create a test project") == true ||
                        promptText?.Contains("xUnit") == true)
                    {
                        operationOrder.Add("TemplateOption");
                    }

                    return choices.Cast<object>().First();
                };
                return testInteractionService;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Verify that template version was prompted before template options
        Assert.Contains("TemplateVersion", operationOrder);

        // If template options were prompted, they should come after version selection
        var versionIndex = operationOrder.IndexOf("TemplateVersion");
        var optionIndex = operationOrder.IndexOf("TemplateOption");

        if (optionIndex >= 0)
        {
            Assert.True(versionIndex < optionIndex,
                $"Template version should be prompted before template options. Order: {string.Join(", ", operationOrder)}");
        }
    }

    [Fact]
    public async Task NewCommandEscapesMarkupInProjectNameAndOutputPath()
    {
        // This test validates that project names containing Spectre markup characters
        // (like '[' and ']') are properly escaped when displayed as default values in prompts.
        // This prevents crashes when the markup parser encounters malformed markup.
        
        var projectNameWithMarkup = "[27;5;13~";  // Example of input that could crash the markup parser
        var capturedProjectNameDefault = string.Empty;
        var capturedOutputPathDefault = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                // Simulate user entering a project name with markup characters
                prompter.PromptForProjectNameCallback = (defaultName) =>
                {
                    capturedProjectNameDefault = defaultName;
                    return projectNameWithMarkup;
                };

                // Capture what default value is passed for the output path
                // The path passed to this callback is the unescaped version
                prompter.PromptForOutputPathCallback = (path) =>
                {
                    capturedOutputPathDefault = path;
                    // Return the path as-is - the escaping is handled internally by PromptForOutputPath
                    return path;
                };

                return prompter;
            };

        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Verify that the default output path was derived from the project name with markup characters
        // The path parameter passed to the callback contains the unescaped markup characters
        var expectedPath = $"./[27;5;13~";
        Assert.Equal(expectedPath, capturedOutputPathDefault);
    }

    [Fact]
    public async Task NewCommandWithoutTemplateCanCreateTypeScriptEmptyTemplate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffoldedLanguageId = string.Empty;
        (string Name, string Description)[]? promptedTemplates = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                    promptText == "Which language would you like to use?"
                        ? choices.Cast<object>().Single(choice => choiceFormatter(choice).Contains("TypeScript", StringComparison.Ordinal))
                        : choices.Cast<object>().First()
            };
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                {
                    promptedTemplates = templates.Select(t => (t.Name, t.Description)).ToArray();
                    return templates.Single(t => t.Name.Equals(KnownTemplateId.CSharpEmptyAppHost, StringComparison.OrdinalIgnoreCase));
                };

                return prompter;
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldedLanguageId = context.Language.LanguageId.Value;
                File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, "apphost.ts"), "// test apphost");
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(KnownLanguageId.TypeScript, scaffoldedLanguageId);
        Assert.NotNull(promptedTemplates);
        Assert.Contains((KnownTemplateId.CSharpEmptyAppHost, "Empty AppHost (Choose language...)"), promptedTemplates);
        Assert.DoesNotContain((KnownTemplateId.TypeScriptEmptyAppHost, "Empty (TypeScript AppHost)"), promptedTemplates);
        Assert.DoesNotContain((KnownTemplateId.JavaEmptyAppHost, "Empty (Java AppHost)"), promptedTemplates);
        Assert.Contains((KnownTemplateId.TypeScriptStarter, "Starter App (Express/React, TypeScript AppHost)"), promptedTemplates);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")));
    }

    [Fact]
    public void NewCommandTemplateSubcommandsListTechnicalNamesForNonInteractiveFlows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.FeatureFlagsFactory = _ => new TestFeatures().SetFeature(KnownFeatures.ShowAllTemplates, true);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();

        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == "aspire-test");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.DotNetEmptyAppHost && subcommand.Description == "Empty (C# AppHost, dotnet template)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.CSharpEmptyAppHost && subcommand.Description == "Empty AppHost (Choose language...)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.TypeScriptEmptyAppHost && subcommand.Description == "Empty (TypeScript AppHost)");
    }

    [Fact]
    public async Task NewCommandWithoutTemplatePromptsWithSingleGenericEmptyTemplate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string[]? promptedTemplateDescriptions = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotGo, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotPython, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotRust, true);
                return features;
            };
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                {
                    promptedTemplateDescriptions = templates
                        .Where(t => t.IsEmpty)
                        .Select(t => t.Description)
                        .ToArray();
                    return templates.Single(t => t.Name.Equals(KnownTemplateId.CSharpEmptyAppHost, StringComparison.OrdinalIgnoreCase));
                };

                return prompter;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(promptedTemplateDescriptions);
        Assert.Equal(["Empty AppHost (Choose language...)"], promptedTemplateDescriptions);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateOmitsDisabledLanguagesFromLanguagePrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string[]? promptedLanguages = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                {
                    if (promptText == "Which language would you like to use?")
                    {
                        promptedLanguages = choices.Cast<object>()
                            .Select(choice => choiceFormatter(choice))
                            .ToArray();
                    }

                    return choices.Cast<object>().First();
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(promptedLanguages);
        Assert.Contains(KnownLanguageId.CSharpDisplayName, promptedLanguages);
        Assert.Contains("TypeScript (Node.js)", promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.PythonDisplayName, promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.JavaDisplayName, promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.GoDisplayName, promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.RustDisplayName, promptedLanguages);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplatePromptsForEnabledLanguages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string[]? promptedLanguages = null;
        string? scaffoldedLanguageId = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                features.SetFeature(KnownFeatures.ExperimentalPolyglotPython, true);
                return features;
            };
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                {
                    var formattedChoices = choices.Cast<object>()
                        .Select(choice => choiceFormatter(choice))
                        .ToArray();

                    if (promptText == "Which language would you like to use?")
                    {
                        promptedLanguages = formattedChoices;
                        return choices.Cast<object>().Single(choice => string.Equals(choiceFormatter(choice), KnownLanguageId.JavaDisplayName, StringComparison.Ordinal));
                    }

                    return choices.Cast<object>().First();
                }
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldedLanguageId = context.Language.LanguageId.Value;
                File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, "AppHost.java"), "package aspire;");
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(KnownLanguageId.Java, scaffoldedLanguageId);
        Assert.NotNull(promptedLanguages);
        Assert.Contains(KnownLanguageId.CSharpDisplayName, promptedLanguages);
        Assert.Contains("TypeScript (Node.js)", promptedLanguages);
        Assert.Contains(KnownLanguageId.PythonDisplayName, promptedLanguages);
        Assert.Contains(KnownLanguageId.JavaDisplayName, promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.GoDisplayName, promptedLanguages);
        Assert.DoesNotContain(KnownLanguageId.RustDisplayName, promptedLanguages);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateIgnoresConfiguredLanguage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"), """
            {
              "language": "typescript/nodejs"
            }
            """);

        var languagePrompted = false;
        string? scaffoldedLanguageId = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                {
                    if (promptText == "Which language would you like to use?")
                    {
                        languagePrompted = true;
                        return choices.Cast<object>().Single(choice => string.Equals(choiceFormatter(choice), KnownLanguageId.JavaDisplayName, StringComparison.Ordinal));
                    }

                    return choices.Cast<object>().First();
                }
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldedLanguageId = context.Language.LanguageId.Value;
                File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, "AppHost.java"), "package aspire;");
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(languagePrompted);
        Assert.Equal(KnownLanguageId.Java, scaffoldedLanguageId);
    }

    [Fact]
    public async Task NewCommandWithExplicitJavaEmptyTemplateCreatesJavaAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? scaffoldedLanguageId = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };

        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldedLanguageId = context.Language.LanguageId.Value;
                File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, "AppHost.java"), "package aspire;");
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-java-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(KnownLanguageId.Java, scaffoldedLanguageId);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.java")));
    }

    [Fact]
    public async Task NewCommandWithExplicitPythonEmptyTemplateCreatesPythonAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? scaffoldedLanguageId = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotPython, true);
                return features;
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldedLanguageId = context.Language.LanguageId.Value;
                File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, "apphost.py"), "# test apphost");
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-py-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(KnownLanguageId.Python, scaffoldedLanguageId);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.py")));
    }

    [Fact]
    public async Task NewCommandWithExplicitCSharpEmptyTemplateCreatesCSharpAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CreateServiceCollection(workspace);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateAndCSharpPromptsForLocalhostTldAndUsesConfirmation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var localhostPrompted = false;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (promptText, defaultValue) =>
                {
                    if (string.Equals(promptText, TemplatingStrings.UseLocalhostTld_Prompt, StringComparison.Ordinal))
                    {
                        localhostPrompted = true;
                        Assert.False(defaultValue);
                        return true;
                    }

                    return false;
                }
            };
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                    templates.Single(t => t.Name.Equals("aspire-empty", StringComparison.OrdinalIgnoreCase));

                return prompter;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(localhostPrompted);

        var runProfilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        Assert.True(File.Exists(runProfilePath));
        var runProfile = await File.ReadAllTextAsync(runProfilePath);
        Assert.Contains("testapp.dev.localhost", runProfile);
        Assert.DoesNotContain("://localhost", runProfile);
    }

    [Fact]
    public async Task NewCommandWithTypeScriptEmptyTemplateUsesScaffolding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffoldingInvoked = false;

        var services = CreateServiceCollection(workspace);

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldingInvoked = true;
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scaffoldingInvoked);
    }

    [Fact]
    public async Task NewCommandWithTypeScriptEmptyTemplatePassesResolvedVersionAndChannelToScaffolding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? scaffoldSdkVersion = null;
        string? scaffoldChannel = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.PackagingServiceFactory = (sp) =>
            {
                var packagingService = new TestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var stableCache = new FakeNuGetPackageCache();
                    stableCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        var package = new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.2.0" };
                        return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                    };

                    var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Both, [], stableCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>([stableChannel]);
                };

                return packagingService;
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldSdkVersion = context.SdkVersion;
                scaffoldChannel = context.Channel;
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output . --channel stable --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("9.2.0", scaffoldSdkVersion);
        Assert.Equal("stable", scaffoldChannel);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateNormalizesDefaultOutputPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedTargetDirectory = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                // Accept the default "./TestApp" path from the prompt
                prompter.PromptForOutputPathCallback = (path) => path;

                return prompter;
            };

        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                capturedTargetDirectory = context.TargetDirectory.FullName;
                return Task.FromResult(true);
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        // Do not pass --output so the default "./TestApp" path is used via the prompter
        var result = command.Parse("new aspire-ts-empty --name TestApp --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedTargetDirectory);

        // The output path should be properly normalized without "./" segments
        Assert.DoesNotContain("./", capturedTargetDirectory);
        Assert.DoesNotContain(".\\", capturedTargetDirectory);

        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestApp");
        Assert.Equal(expectedPath, capturedTargetDirectory);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateAndTypeScriptPromptsForLocalhostTldAndUsesConfirmation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffoldingInvoked = false;
        var localhostPrompted = false;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (promptText, defaultValue) =>
                {
                    if (string.Equals(promptText, TemplatingStrings.UseLocalhostTld_Prompt, StringComparison.Ordinal))
                    {
                        localhostPrompted = true;
                        Assert.False(defaultValue);
                        return true;
                    }

                    return false;
                }
            };
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                    templates.Single(t => t.Name.Equals("aspire-ts-empty", StringComparison.OrdinalIgnoreCase));

                return prompter;
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = async (context, cancellationToken) =>
            {
                scaffoldingInvoked = true;
                await File.WriteAllTextAsync(Path.Combine(context.TargetDirectory.FullName, "aspire.config.json"), """
                    {
                      "appHost": {
                        "path": "apphost.ts",
                        "language": "typescript/nodejs"
                      },
                      "profiles": {
                        "https": {
                          "applicationUrl": "https://localhost:1234;http://localhost:5678",
                          "environmentVariables": {
                            "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:8765",
                            "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:4321"
                          }
                        }
                      }
                    }
                    """, cancellationToken);
                return true;
            }
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scaffoldingInvoked);
        Assert.True(localhostPrompted);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("testapp.dev.localhost", configContent);
        Assert.DoesNotContain("://localhost", configContent);
    }

    [Fact]
    public async Task NewCommandWithTypeScriptStarterGeneratesSdkArtifacts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var buildAndGenerateCalled = false;
        string? channelSeenByProject = null;
        string? sdkVersionSeenByProject = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, runnerOptions, cancellationToken) =>
                {
                    var package = new NuGetPackage
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (0, new NuGetPackage[] { package });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = cancellationToken =>
                {
                    var dailyCache = new FakeNuGetPackageCache
                    {
                        GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                        {
                            var package = new NuGetPackage
                            {
                                Id = "Aspire.ProjectTemplates",
                                Source = "nuget",
                                Version = "9.2.0"
                            };

                            return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                        }
                    };

                    var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [], dailyCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]);
                }
            };
        });

        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((directory, cancellationToken) =>
        {
            buildAndGenerateCalled = true;
            var config = AspireConfigFile.Load(directory.FullName);
            channelSeenByProject = config?.Channel;
            sdkVersionSeenByProject = config?.SdkVersion;

            var modulesDir = Directory.CreateDirectory(Path.Combine(directory.FullName, ".modules"));
            File.WriteAllText(Path.Combine(modulesDir.FullName, "aspire.ts"), "// generated sdk");

            return Task.FromResult(true);
        }));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-starter --name TestApp --output . --channel daily --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(buildAndGenerateCalled);
        Assert.Equal("daily", channelSeenByProject);
        Assert.Equal("9.2.0", sdkVersionSeenByProject);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".modules", "aspire.ts")));
    }

    [Fact]
    public async Task NewCommandWithTypeScriptStarterReturnsFailedToBuildArtifactsWhenSdkGenerationFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService();

        var services = CreateServiceCollection(workspace, options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, runnerOptions, cancellationToken) =>
                {
                    var package = new NuGetPackage
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (0, new NuGetPackage[] { package });
                }
            };

            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = cancellationToken =>
                {
                    var dailyCache = new FakeNuGetPackageCache
                    {
                        GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                        {
                            var package = new NuGetPackage
                            {
                                Id = "Aspire.ProjectTemplates",
                                Source = "nuget",
                                Version = "9.2.0"
                            };

                            return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                        }
                    };

                    var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [], dailyCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]);
                }
            };
        });

        services.AddSingleton<IInteractionService>(interactionService);
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((directory, cancellationToken) => Task.FromResult(false)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-starter --name TestApp --output . --channel daily --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToBuildArtifacts, exitCode);
        Assert.Collection(interactionService.DisplayedErrors,
            error => Assert.Equal("Automatic 'aspire restore' failed for the new TypeScript starter project. Run 'aspire restore' in the project directory for more details.", error));
    }

    [Fact]
    public async Task NewCommandNonInteractiveDoesNotPrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            // Configure non-interactive host environment
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        // Before the fix, this would throw InvalidOperationException with
        // "Interactive input is not supported in this environment" because
        // GetTemplates() did not pass the nonInteractive flag, causing
        // the template to try to prompt for options.
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task NewCommandNonInteractiveWithoutTemplate_DisplaysErrorWithAvailableTemplates()
    {
        TestInteractionService? testInteractionService = null;
        string? availableTemplatesMessage = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

            options.InteractionServiceFactory = (sp) =>
            {
                testInteractionService = new TestInteractionService
                {
                    DisplaySubtleMessageCallback = message => availableTemplatesMessage = message
                };
                return testInteractionService;
            };

            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };

        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.MissingRequiredArgument, exitCode);
        Assert.NotNull(testInteractionService);
        Assert.Contains(testInteractionService.DisplayedErrors,
            e => string.Equals(e, NewCommandStrings.NonInteractiveTemplateRequired, StringComparison.Ordinal));
        Assert.NotNull(availableTemplatesMessage);
        Assert.Contains(KnownTemplateId.TypeScriptEmptyAppHost, availableTemplatesMessage);
        Assert.Contains(KnownTemplateId.JavaEmptyAppHost, availableTemplatesMessage);
    }

    [Fact]
    public async Task NewCommandNonInteractiveUsesDefaultNameWhenNotProvided()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedProjectName = null;
        string? capturedOutputPath = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    capturedProjectName = projectName;
                    capturedOutputPath = outputPath;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        // Neither --name nor --output is provided, so both use their defaults
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        // The default project name is derived from the workspace directory name
        Assert.Equal(workspace.WorkspaceRoot.Name, capturedProjectName);
        // The default output path ends with the project name subdirectory
        Assert.Equal(workspace.WorkspaceRoot.Name, Path.GetFileName(capturedOutputPath));
    }

    [Fact]
    public async Task NewCommandNonInteractiveWithAllOptions_Succeeds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedProjectName = null;
        string? capturedOutputPath = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    capturedProjectName = projectName;
                    capturedOutputPath = outputPath;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse($"new aspire-starter --name MyProject --output {Path.Combine(workspace.WorkspaceRoot.FullName, "my-project")} --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("MyProject", capturedProjectName);
        Assert.NotNull(capturedOutputPath);
        Assert.Contains("my-project", capturedOutputPath);

        // Agent init runs by default after project creation
        var skillPath = Path.Combine(capturedOutputPath, ".agents", "skills", "aspire", "SKILL.md");
        Assert.True(File.Exists(skillPath));
    }

    [Fact]
    public async Task NewCommandNonInteractiveWithAllOptions_SuppressAgentInitTrue_SkipsAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse($"new aspire-starter --name MyProject --output {workspace.WorkspaceRoot.FullName} --use-redis-cache --test-framework None --suppress-agent-init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Agent init should not have run — no skill files should exist
        var skillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.False(File.Exists(skillPath));
    }

    [Fact]
    public async Task NewCommand_WhenCSharpTemplateApplyFails_DisplaysCreationErrorMessage()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestNewCommandPrompter(interactionService);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 1; // Simulate failure
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedMessage = string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreationFailed, 1, executionContext.LogFilePath);

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(testInteractionService);
        Assert.Contains(expectedMessage, testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task NewCommand_WhenTypeScriptTemplateApplyFails_ReturnsNonZeroExitCode()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                return Task.FromResult(false); // Simulate failure for TypeScript template
            }
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(testInteractionService);
    }

    [Fact]
    public async Task NewCommandInExtensionModeAppendsProjectNameToOutputPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedOutputPath = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel
            {
                HasCapabilityAsyncCallback = (c, _) => Task.FromResult(c is "baseline.v1"),
            };

            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (_) => "MyFirstApp";

                // Simulate the user picking a parent folder (not named after the project)
                prompter.PromptForOutputPathCallback = (_) =>
                    Path.Combine(workspace.WorkspaceRoot.FullName, "source");

                return prompter;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    capturedOutputPath = outputPath;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedOutputPath);

        // Output path should have the project name appended as a subdirectory
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "source", "MyFirstApp");
        Assert.Equal(expectedPath, capturedOutputPath);
    }

    [Fact]
    public async Task NewCommandInExtensionModeDoesNotDoubleAppendProjectName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedOutputPath = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel
            {
                HasCapabilityAsyncCallback = (c, _) => Task.FromResult(c is "baseline.v1"),
            };

            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (_) => "MyFirstApp";

                // Simulate the user picking a folder already named after the project
                prompter.PromptForOutputPathCallback = (_) =>
                    Path.Combine(workspace.WorkspaceRoot.FullName, "source", "MyFirstApp");

                return prompter;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    capturedOutputPath = outputPath;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedOutputPath);

        // Output path should NOT have the project name double-appended
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "source", "MyFirstApp");
        Assert.Equal(expectedPath, capturedOutputPath);
    }

    [Fact]
    public async Task NewCommandInConsoleModeDoesNotAppendProjectName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedOutputPath = null;

        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            // Use TestInteractionService (not ExtensionInteractionService) to stay in console/non-extension mode
            options.InteractionServiceFactory = _ => new TestInteractionService();
            // Default InteractionServiceFactory creates ConsoleInteractionService (not extension mode)

            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                prompter.PromptForProjectNameCallback = (_) => "MyFirstApp";

                // Simulate user accepting default path or selecting parent folder
                prompter.PromptForOutputPathCallback = (_) =>
                    Path.Combine(workspace.WorkspaceRoot.FullName, "source");

                return prompter;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = CreateTestRunnerWithStandardPackages();
                runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                {
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    capturedOutputPath = outputPath;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedOutputPath);

        // In console mode, the output path should NOT have project name appended
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "source");
        Assert.Equal(expectedPath, capturedOutputPath);
    }

    [Fact]
    public async Task NewCommandInExtensionModeHandlesTrailingDirectorySeparator()
    {
        const string projectName = "MyFirstApp";

        async Task AssertOutputPathAsync(Func<string, string> selectedPathFactory, Func<string, string> expectedPathFactory)
        {
            using var workspace = TemporaryWorkspace.Create(outputHelper);
            string? capturedOutputPath = null;

            var services = CreateServiceCollection(workspace, options =>
            {
                options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
                options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
                options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel
                {
                    HasCapabilityAsyncCallback = (c, _) => Task.FromResult(c is "baseline.v1"),
                };

                options.NewCommandPrompterFactory = (sp) =>
                {
                    var interactionService = sp.GetRequiredService<IInteractionService>();
                    var prompter = new TestNewCommandPrompter(interactionService);

                    prompter.PromptForProjectNameCallback = (_) => projectName;

                    prompter.PromptForOutputPathCallback = (_) =>
                        selectedPathFactory(workspace.WorkspaceRoot.FullName);

                    return prompter;
                };

                options.DotNetCliRunnerFactory = (sp) =>
                {
                    var runner = CreateTestRunnerWithStandardPackages();
                    runner.InstallTemplateAsyncCallback = (packageName, version, nugetSource, force, invocationOptions, ct) =>
                    {
                        return (0, version);
                    };
                    runner.NewProjectAsyncCallback = (templateName, pName, outputPath, invocationOptions, ct) =>
                    {
                        capturedOutputPath = outputPath;
                        return 0;
                    };
                    return runner;
                };
            });
            using var provider = services.BuildServiceProvider();

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.Success, exitCode);
            Assert.NotNull(capturedOutputPath);
            Assert.Equal(expectedPathFactory(workspace.WorkspaceRoot.FullName), capturedOutputPath);
        }

        // Trailing separator on a parent folder should still append the project name once.
        await AssertOutputPathAsync(
            workspaceRoot => Path.Combine(workspaceRoot, "source") + Path.DirectorySeparatorChar,
            workspaceRoot => Path.Combine(workspaceRoot, "source", projectName));

        // Trailing separator on a folder already named after the project should not double-append.
        await AssertOutputPathAsync(
            workspaceRoot => Path.Combine(workspaceRoot, projectName) + Path.DirectorySeparatorChar,
            workspaceRoot => Path.Combine(workspaceRoot, projectName));
    }

    [Fact]
    public async Task NewCommandNonInteractive_SuppressAgentInitTrue_SkipsAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output . --suppress-agent-init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Agent init should not have run — no skill files should exist
        var skillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.False(File.Exists(skillPath));
    }

    [Fact]
    public async Task NewCommandNonInteractive_SuppressAgentInitFalse_RunsAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output . --suppress-agent-init=false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Agent init should have run — default skill files should exist
        var skillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.True(File.Exists(skillPath));
    }

    [Fact]
    public async Task NewCommandNonInteractive_NoSuppressAgentInitOption_DefaultsToRunAgentInit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CreateServiceCollection(workspace, options =>
        {
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Default is to run agent init
        var skillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.True(File.Exists(skillPath));
    }
}
