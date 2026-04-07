// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Utils;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;
using Aspire.Cli.Backchannel;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Commands;

public class NewCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NewCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void NewCommandWithPolyglotEnabled_ExposesTemplateSubcommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        Assert.NotEmpty(command.Subcommands);
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.CSharpEmptyAppHost && subcommand.Description == "Empty AppHost");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.TypeScriptEmptyAppHost && subcommand.Description == "Empty (TypeScript AppHost)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.JavaEmptyAppHost && subcommand.Description == "Empty (Java AppHost)");
    }

    [Fact]
    public void NewCommandWithPolyglotDisabled_ExposesTemplateSubcommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        Assert.NotEmpty(command.Subcommands);
        Assert.DoesNotContain(command.Options, option => option.Aliases.Contains("--language", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NewCommandInteractiveFlowSmokeTest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Set of options that we'll give when prompted.
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestNewCommandPrompter(interactionService);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    // Quarantined due to flakiness. See linked issue for details.
    public async Task NewCommandDerivesOutputPathFromProjectNameForStarterTemplate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {

            // Set of options that we'll give when prompted.
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForProjectNameIfSpecifiedOnCommandLine()
    {
        var promptedForName = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {

            // Set of options that we'll give when prompted.
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForName);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForOutputPathIfSpecifiedOnCommandLine()
    {
        bool promptedForPath = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {

            // Set of options that we'll give when prompted.
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --output notsrc --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForPath);
    }

    [Fact]
    public async Task NewCommandWithChannelOptionUsesSpecifiedChannel()
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
                var packagingService = new NewCommandTestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var stableCache = new NewCommandTestFakeNuGetPackageCache();
                    stableCache.GetTemplatePackagesAsyncCallback = (dir, prerelease, nugetConfig, ct) =>
                    {
                        channelNameUsed = "stable";
                        var package = new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "9.2.0" };
                        return Task.FromResult<IEnumerable<NuGetPackage>>([package]);
                    };
                    
                    var dailyCache = new NewCommandTestFakeNuGetPackageCache();
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
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --channel stable --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("stable", channelNameUsed); // Verify the stable channel was used
        Assert.False(promptedForVersion); // Should not prompt when --channel is specified
    }

    [Fact]
    public async Task NewCommandWithChannelOptionAutoSelectsHighestVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
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
                    throw new InvalidOperationException("Should not prompt for version when --channel is specified");
                };
                
                return prompter;
            };

            options.PackagingServiceFactory = (sp) =>
            {
                var packagingService = new NewCommandTestPackagingService();
                packagingService.GetChannelsAsyncCallback = (ct) =>
                {
                    var fakeCache = new NewCommandTestFakeNuGetPackageCache();
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
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --channel stable --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("9.2.0", selectedVersion); // Should auto-select highest version (9.2.0)
        Assert.False(promptedForVersion); // Should not prompt when --channel is specified
    }

    [Fact]
    // Quarantined due to flakiness. See linked issue for details.
    public async Task NewCommandDoesNotPromptForTemplateIfSpecifiedOnCommandLine()
    {
        bool promptedForTemplate = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {

            // Set of options that we'll give when prompted.
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForTemplate);
    }

    [Fact]
    public async Task NewCommandDoesNotPromptForTemplateVersionIfSpecifiedOnCommandLine()
    {
        bool promptedForTemplateVersion = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {

            // Set of options that we'll give when prompted.
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetConfigFile, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name MyApp --output . --use-redis-cache --test-framework None --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
        Assert.Equal(0, exitCode);
        Assert.False(promptedForTemplateVersion);
    }

    [Fact]
    public async Task NewCommand_EmptyPackageList_DisplaysErrorMessage()
    {
        TestInteractionService? testInteractionService = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {
            options.InteractionServiceFactory = (sp) => {
                testInteractionService = new TestInteractionService();
                return testInteractionService;
            };

            options.DotNetCliRunnerFactory = (sp) => {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) => {
                    return (0, Array.Empty<NuGetPackage>());
                };
                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NewCommandPrompterFactory = (sp) => {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                return prompter;
            };
            options.CertificateServiceFactory = _ => new ThrowingCertificateService();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

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
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToTrustCertificates, exitCode);
    }

    [Fact]
    public async Task NewCommandWithExitCode73ShowsUserFriendlyError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => {

            // Set of options that we'll give when prompted.
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestNewCommandPrompter(interactionService);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

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
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToCreateNewProject, exitCode);
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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetConfigFile, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-starter --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
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

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-starter --use-redis-cache --test-framework None");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);

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

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) => choices.Cast<object>().First()
            };
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, dotnetOptions, cancellationToken) =>
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
            };
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                {
                    promptedTemplates = templates.Select(t => (t.Name, t.Description)).ToArray();
                    return templates.Single(t => t.Name.Equals(KnownTemplateId.TypeScriptEmptyAppHost, StringComparison.OrdinalIgnoreCase));
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.Equal(KnownLanguageId.TypeScript, scaffoldedLanguageId);
        Assert.NotNull(promptedTemplates);
        Assert.Contains((KnownTemplateId.CSharpEmptyAppHost, "Empty AppHost"), promptedTemplates);
        Assert.Contains((KnownTemplateId.TypeScriptEmptyAppHost, "Empty (TypeScript AppHost)"), promptedTemplates);
        Assert.Contains((KnownTemplateId.TypeScriptStarter, "Starter App (Express/React)"), promptedTemplates);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts")));
    }

    [Fact]
    public void NewCommandTemplateSubcommandsListTechnicalNamesForNonInteractiveFlows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FeatureFlagsFactory = _ => new NewCommandTestFeatures(showAllTemplates: true);
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();

        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == "aspire-test");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.DotNetEmptyAppHost && subcommand.Description == "Empty (C# AppHost, dotnet template)");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.CSharpEmptyAppHost && subcommand.Description == "Empty AppHost");
        Assert.Contains(command.Subcommands, subcommand => subcommand.Name == KnownTemplateId.TypeScriptEmptyAppHost && subcommand.Description == "Empty (TypeScript AppHost)");
    }

    [Fact]
    public async Task NewCommandWithoutTemplatePromptsWithDistinctLanguageSpecificEmptyDescriptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string[]? promptedTemplateDescriptions = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);
                prompter.PromptForTemplateCallback = templates =>
                {
                    promptedTemplateDescriptions = templates
                        .Where(t => t.Name is KnownTemplateId.CSharpEmptyAppHost or KnownTemplateId.TypeScriptEmptyAppHost)
                        .Select(t => t.Description)
                        .ToArray();
                    return templates.Single(t => t.Name.Equals(KnownTemplateId.CSharpEmptyAppHost, StringComparison.OrdinalIgnoreCase));
                };

                return prompter;
            };
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, dotnetOptions, cancellationToken) =>
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
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.NotNull(promptedTemplateDescriptions);
        Assert.Contains("Empty AppHost", promptedTemplateDescriptions);
        Assert.Contains("Empty (TypeScript AppHost)", promptedTemplateDescriptions);
    }

    [Fact]
    public async Task NewCommandWithExplicitJavaEmptyTemplateCreatesJavaAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? scaffoldedLanguageId = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FeatureFlagsFactory = _ =>
            {
                var features = new TestFeatures();
                features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
                return features;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-java-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.Equal(KnownLanguageId.Java, scaffoldedLanguageId);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.java")));
    }

    [Fact]
    public async Task NewCommandWithExplicitCSharpEmptyTemplateCreatesCSharpAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateAndCSharpPromptsForLocalhostTldAndUsesSelection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var localhostPrompted = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (_, _) => false,
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                {
                    if (string.Equals(promptText, TemplatingStrings.UseLocalhostTld_Prompt, StringComparison.Ordinal))
                    {
                        localhostPrompted = true;
                        return choices.Cast<object>().Single(choice =>
                            string.Equals(choiceFormatter(choice), TemplatingStrings.Yes, StringComparisons.CliInputOrOutput));
                    }

                    return choices.Cast<object>().First();
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
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
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

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                scaffoldingInvoked = true;
                return Task.FromResult(true);
            }
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output . --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.True(scaffoldingInvoked);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateNormalizesDefaultOutputPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedTargetDirectory = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestNewCommandPrompter(interactionService);

                // Accept the default "./TestApp" path from the prompt
                prompter.PromptForOutputPathCallback = (path) => path;

                return prompter;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        // Do not pass --output so the default "./TestApp" path is used via the prompter
        var result = command.Parse("new aspire-ts-empty --name TestApp --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedTargetDirectory);

        // The output path should be properly normalized without "./" segments
        Assert.DoesNotContain("./", capturedTargetDirectory);
        Assert.DoesNotContain(".\\", capturedTargetDirectory);

        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestApp");
        Assert.Equal(expectedPath, capturedTargetDirectory);
    }

    [Fact]
    public async Task NewCommandWithEmptyTemplateAndTypeScriptPromptsForLocalhostTldAndUsesSelection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scaffoldingInvoked = false;
        var localhostPrompted = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (_, _) => false,
                PromptForSelectionCallback = (promptText, choices, choiceFormatter, cancellationToken) =>
                {
                    if (string.Equals(promptText, TemplatingStrings.UseLocalhostTld_Prompt, StringComparison.Ordinal))
                    {
                        localhostPrompted = true;
                        return choices.Cast<object>().Single(choice =>
                            string.Equals(choiceFormatter(choice), TemplatingStrings.Yes, StringComparisons.CliInputOrOutput));
                    }

                    return choices.Cast<object>().First();
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
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
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

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, runnerOptions, cancellationToken) =>
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

            options.PackagingServiceFactory = _ => new NewCommandTestPackagingService
            {
                GetChannelsAsyncCallback = cancellationToken =>
                {
                    var dailyCache = new NewCommandTestFakeNuGetPackageCache
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
            var config = AspireJsonConfiguration.Load(directory.FullName);
            channelSeenByProject = config?.Channel;

            var modulesDir = Directory.CreateDirectory(Path.Combine(directory.FullName, ".modules"));
            File.WriteAllText(Path.Combine(modulesDir.FullName, "aspire.ts"), "// generated sdk");

            return Task.FromResult(true);
        }));

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("new aspire-ts-starter --name TestApp --output . --channel daily --localhost-tld false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.True(buildAndGenerateCalled);
        Assert.Equal("daily", channelSeenByProject);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".modules", "aspire.ts")));
    }

    [Fact]
    public async Task NewCommandWithTypeScriptStarterReturnsFailedToBuildArtifactsWhenSdkGenerationFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, runnerOptions, cancellationToken) =>
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

            options.PackagingServiceFactory = _ => new NewCommandTestPackagingService
            {
                GetChannelsAsyncCallback = cancellationToken =>
                {
                    var dailyCache = new NewCommandTestFakeNuGetPackageCache
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

        var provider = services.BuildServiceProvider();
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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Configure non-interactive host environment
            options.CliHostEnvironmentFactory = (sp) =>
            {
                var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { package } // Single package.
                        );
                };

                return runner;
            };
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-empty --name TestApp --output .");

        // Before the fix, this would throw InvalidOperationException with
        // "Interactive input is not supported in this environment" because
        // GetTemplates() did not pass the nonInteractive flag, causing
        // the template to try to prompt for options.
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task NewCommand_WhenCSharpTemplateApplyFails_DisplaysCreationErrorMessage()
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

            options.NewCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestNewCommandPrompter(interactionService);
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var package = new NuGetPackage()
                    {
                        Id = "Aspire.ProjectTemplates",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (0, new NuGetPackage[] { package });
                };

                runner.NewProjectAsyncCallback = (templateName, projectName, outputPath, invocationOptions, ct) =>
                {
                    return 1; // Simulate failure
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

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
                runner.SearchPackagesAsyncCallback = (dir, query, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
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
            };
        });

        services.AddSingleton<IScaffoldingService>(new TestScaffoldingService
        {
            ScaffoldAsyncCallback = (context, cancellationToken) =>
            {
                return Task.FromResult(false); // Simulate failure for TypeScript template
            }
        });

        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<NewCommand>();
        var result = command.Parse("new aspire-ts-empty --name TestApp --output .");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(testInteractionService);
    }
}

internal sealed class TestNewCommandPrompter(IInteractionService interactionService) : NewCommandPrompter(interactionService)
{
    public Func<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>, (NuGetPackage Package, PackageChannel Channel)>? PromptForTemplatesVersionCallback { get; set; }
    public Func<ITemplate[], ITemplate>? PromptForTemplateCallback { get; set; }
    public Func<string, string>? PromptForProjectNameCallback { get; set; }
    public Func<string, string>? PromptForOutputPathCallback { get; set; }

    public override Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken)
    {
        return PromptForTemplateCallback switch
        {
            { } callback => Task.FromResult(callback(validTemplates)),
            _ => Task.FromResult(validTemplates[0]) // If no callback is provided just accept the first template.
        };
    }

    public override Task<string> PromptForProjectNameAsync(string defaultName, CancellationToken cancellationToken)
    {
        return PromptForProjectNameCallback switch
        {
            { } callback => Task.FromResult(callback(defaultName)),
            _ => Task.FromResult(defaultName) // If no callback is provided just accept the default.
        };
    }

    public override Task<string> PromptForOutputPath(string path, CancellationToken cancellationToken)
    {
        return PromptForOutputPathCallback switch
        {
            { } callback => Task.FromResult(callback(path)),
            _ => Task.FromResult(path) // If no callback is provided just accept the default.
        };
    }

    public override Task<(NuGetPackage Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(NuGetPackage Package, PackageChannel Channel)> candidatePackages, CancellationToken cancellationToken)
    {
        return PromptForTemplatesVersionCallback switch
        {
            { } callback => Task.FromResult(callback(candidatePackages)),
            _ => Task.FromResult(candidatePackages.First()) // If no callback is provided just accept the first package.
        };
    }
}

internal sealed class OrderTrackingInteractionService(List<string> operationOrder) : IInteractionService
{
    public ConsoleOutput Console { get; set; }

    public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        return action();
    }

    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        action();
    }

    public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(defaultValue ?? string.Empty);
    }

    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        // Track template option prompts
        if (promptText?.Contains("Redis") == true ||
            promptText?.Contains("test framework") == true ||
            promptText?.Contains("Create a test project") == true ||
            promptText?.Contains("xUnit") == true)
        {
            operationOrder.Add("TemplateOption");
        }

        return Task.FromResult(choices.First());
    }

    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        if (preSelected is not null)
        {
            return Task.FromResult<IReadOnlyList<T>>(preSelected.ToList());
        }

        return Task.FromResult<IReadOnlyList<T>>(choices.ToList());
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) => 0;
    public void DisplayError(string errorMessage) { }
    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false) { }
    public void DisplaySuccess(string message, bool allowMarkup = false) { }
    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines) { }
    public void DisplayCancellationMessage() { }
    public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<string> PromptForFilePathAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, CancellationToken cancellationToken = default)
        => PromptForStringAsync(promptText, defaultValue, validator, isSecret: false, required, cancellationToken);
    public void DisplaySubtleMessage(string message, bool escapeMarkup = true) { }
    public void DisplayEmptyLine() { }
    public void DisplayPlainText(string text) { }
    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null) { }
    public void DisplayMarkdown(string markdown) { }
    public void DisplayMarkupLine(string markup) { }
    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false) { }
    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null) { }
    public void DisplayRenderable(IRenderable renderable) { }
    public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback) => callback(_ => { });
}

internal sealed class NewCommandTestPackagingService : IPackagingService
{
    public Func<CancellationToken, Task<IEnumerable<PackageChannel>>>? GetChannelsAsyncCallback { get; set; }

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (GetChannelsAsyncCallback is not null)
        {
            return GetChannelsAsyncCallback(cancellationToken);
        }
        
        // Default: Return a fake channel
        var testChannel = PackageChannel.CreateImplicitChannel(new NewCommandTestFakeNuGetPackageCache());
        return Task.FromResult<IEnumerable<PackageChannel>>(new[] { testChannel });
    }
}

internal sealed class NewCommandTestFakeNuGetPackageCache : INuGetPackageCache
{
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetTemplatePackagesAsyncCallback { get; set; }

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        if (GetTemplatePackagesAsyncCallback is not null)
        {
            return GetTemplatePackagesAsyncCallback(workingDirectory, prerelease, nugetConfigFile, cancellationToken);
        }

        var package = new NuGetPackage
        {
            Id = "Aspire.ProjectTemplates",
            Source = "nuget",
            Version = "10.0.0"
        };
        return Task.FromResult<IEnumerable<NuGetPackage>>(new[] { package });
    }

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<NuGetPackage>>(Array.Empty<NuGetPackage>());
    }

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<NuGetPackage>>(Array.Empty<NuGetPackage>());
    }

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<NuGetPackage>>(Array.Empty<NuGetPackage>());
    }
}

internal sealed class TestScaffoldingService : IScaffoldingService
{
    public Func<ScaffoldContext, CancellationToken, Task<bool>>? ScaffoldAsyncCallback { get; set; }

    public Task<bool> ScaffoldAsync(ScaffoldContext context, CancellationToken cancellationToken)
    {
        if (ScaffoldAsyncCallback is not null)
        {
            return ScaffoldAsyncCallback(context, cancellationToken);
        }

        return Task.FromResult(true);
    }
}

internal sealed class NewCommandTestFeatures(bool showAllTemplates = false) : IFeatures
{
    public bool IsFeatureEnabled(string featureFlag, bool defaultValue)
    {
        return featureFlag switch
        {
            "showAllTemplates" => showAllTemplates,
            _ => defaultValue
        };
    }
}

internal sealed class TestTypeScriptStarterProjectFactory(Func<DirectoryInfo, CancellationToken, Task<bool>> buildAndGenerateSdkAsync) : IAppHostProjectFactory
{
    private readonly TestTypeScriptStarterProject _project = new(buildAndGenerateSdkAsync);

    public IAppHostProject GetProject(LanguageInfo language)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (!string.Equals(language.LanguageId, KnownLanguageId.TypeScript, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"No handler available for language '{language.LanguageId}'.");
        }

        return _project;
    }

    public IAppHostProject? TryGetProject(FileInfo appHostFile)
    {
        return appHostFile.Name.Equals("apphost.ts", StringComparison.OrdinalIgnoreCase) ? _project : null;
    }

    public IAppHostProject GetProject(FileInfo appHostFile)
    {
        return TryGetProject(appHostFile) ?? throw new NotSupportedException($"No handler available for AppHost file '{appHostFile.Name}'.");
    }
}

internal sealed class TestTypeScriptStarterProject(Func<DirectoryInfo, CancellationToken, Task<bool>> buildAndGenerateSdkAsync) : IAppHostProject, IGuestAppHostSdkGenerator
{
    public bool IsUnsupported { get; set; }

    public string LanguageId => KnownLanguageId.TypeScript;

    public string DisplayName => "TypeScript (Node.js)";

    public string? AppHostFileName => "apphost.ts";

    public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string[]>(["apphost.ts"]);
    }

    public bool CanHandle(FileInfo appHostFile)
    {
        return appHostFile.Name.Equals("apphost.ts", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsUsingProjectReferences(FileInfo appHostFile)
    {
        return false;
    }

    public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        return Task.FromResult(new AppHostValidationResult(IsValid: CanHandle(appHostFile)));
    }

    public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
    {
        return Task.FromResult(RunningInstanceResult.NoRunningInstance);
    }

    public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageReferencesAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<bool> BuildAndGenerateSdkAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        return buildAndGenerateSdkAsync(directory, cancellationToken);
    }
}
