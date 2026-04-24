// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class AddCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AddCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("add --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AddCommandInteractiveFlowSmokeTest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestAddCommandPrompter(interactionService);
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var azureRedisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { dockerPackage, redisPackage, azureRedisPackage } //
                        );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    // Simulate adding the package.
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AddCommandDoesNotPromptForIntegrationArgumentIfSpecifiedOnCommandLine()
    {
        var promptedForIntegrationPackages = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegrationPackages = true;
                    throw new InvalidOperationException("Should not have been prompted for integration packages.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var azureRedisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { dockerPackage, redisPackage, azureRedisPackage } //
                        );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    // Simulate adding the package.
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add docker");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForIntegrationPackages);
    }

    [Fact]
    public async Task AddCommandDoesNotPromptForVersionIfSpecifiedOnCommandLine()
    {
        var promptedForIntegrationPackages = false;
        var promptedForVersion = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegrationPackages = true;
                    throw new InvalidOperationException("Should not have been prompted for integration packages.");
                };

                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var azureRedisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { dockerPackage, redisPackage, azureRedisPackage } //
                        );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    // Simulate adding the package.
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add docker --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForIntegrationPackages);
        Assert.False(promptedForVersion);
    }

    [Fact]
    public async Task AddCommandInteractiveDoesNotPromptForVersionIfSpecifiedOnCommandLine()
    {
        var promptedForIntegrationPackages = false;
        var promptedForVersion = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegrationPackages = true;
                    throw new InvalidOperationException("Should not have been prompted for integration packages.");
                };

                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var azureRedisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0,
                        new NuGetPackage[] { dockerPackage, redisPackage, azureRedisPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) => 0;

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add docker --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.False(promptedForIntegrationPackages);
        Assert.False(promptedForVersion);
    }

    [Fact]
    public async Task AddCommandDoesNotPromptForVersionWhenSpecifiedVersionIsFoundViaExactMatchSearch()
    {
        var promptedForVersion = false;
        var selectedPackageVersion = string.Empty;
        var exactMatchQueries = new List<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage
                            {
                                Id = "Aspire.Hosting.Redis",
                                Source = "nuget",
                                Version = "13.3.0"
                            }
                        ]);
                    }

                    exactMatchQueries.Add(query);

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForVersion);
        Assert.Equal("13.2.0", selectedPackageVersion);
        Assert.Equal(2, exactMatchQueries.Count);
        Assert.All(exactMatchQueries, query => Assert.Equal("Aspire.Hosting.Redis", query));
    }

    [Fact]
    public async Task AddCommandInteractiveDoesNotPromptForVersionWhenSpecifiedVersionIsFoundViaExactMatchSearch()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var selectedPackageVersion = string.Empty;
        var exactMatchQueries = new List<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    throw new InvalidOperationException("Should not have been prompted for integration selection.");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage
                            {
                                Id = "Aspire.Hosting.Redis",
                                Source = "nuget",
                                Version = "13.3.0"
                            }
                        ]);
                    }

                    exactMatchQueries.Add(query);

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.Equal("13.2.0", selectedPackageVersion);
        Assert.Equal(2, exactMatchQueries.Count);
        Assert.All(exactMatchQueries, query => Assert.Equal("Aspire.Hosting.Redis", query));
    }

    [Fact]
    public async Task AddCommandSearchesEachPackageIdOnceWhenExactMatchFallsBackAcrossSharedChannel()
    {
        var promptedForVersion = false;
        var selectedPackageVersion = string.Empty;
        var exactMatchQueryCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.3.1" }
                        ]);
                    }

                    exactMatchQueryCounts[query] = exactMatchQueryCounts.GetValueOrDefault(query) + 1;

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForVersion);
        Assert.Equal("13.2.0", selectedPackageVersion);
        Assert.Equal(2, exactMatchQueryCounts["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public async Task AddCommandWithoutIntegrationNameDoesNotPromptForVersionWhenSpecifiedVersionIsFoundViaExactMatchSearch()
    {
        var promptedForVersion = false;
        var selectedPackageVersion = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) => packages.Single(package => package.Package.Id == "Aspire.Hosting.Redis");
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.Docker" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForVersion);
        Assert.Equal("13.2.0", selectedPackageVersion);
    }

    [Fact]
    public async Task AddCommandShowsStatusWhenSearchingForSpecifiedVersionAfterPackageSelection()
    {
        var statusMessages = new List<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService
        {
            ShowStatusCallback = statusMessages.Add
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) => packages.Single(package => package.Package.Id == "Aspire.Hosting.Redis");
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                    throw new InvalidOperationException("Should not have been prompted for integration version.");

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.Docker" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) => 0;

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Contains(AddCommandStrings.SearchingForAspirePackages, statusMessages);
        Assert.Contains(string.Format(AddCommandStrings.SearchingForSpecifiedPackageVersion, "Aspire.Hosting.Redis", "13.2.0"), statusMessages);
    }

    [Fact]
    public async Task AddCommandFailsWhenSpecifiedVersionDoesNotExist()
    {
        var promptedForVersion = false;
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(promptedForVersion);
        Assert.False(addPackageWasCalled);
        Assert.Contains(testInteractionService.DisplayedErrors, error => error.Contains("13.2.0") && error.Contains("Aspire.Hosting.Redis"));
    }

    [Fact]
    public async Task AddCommandInteractiveFailsWhenSpecifiedVersionDoesNotExist()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    throw new InvalidOperationException("Should not have been prompted for integration selection.");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.False(addPackageWasCalled);
        Assert.Contains(testInteractionService.DisplayedErrors, error => error.Contains("13.2.0") && error.Contains("Aspire.Hosting.Redis"));
    }

    [Fact]
    public async Task AddCommandPromptsForDisambiguation()
    {
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? promptedPackages = null;
        string? addedPackageName = null;
        string? addedPackageVersion = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedPackages = packages;
                    return packages.Single(p => p.Package.Id == "Aspire.Hosting.Redis");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var azureRedisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { dockerPackage, redisPackage, azureRedisPackage } //
                        );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackageName = packageName;
                    addedPackageVersion = packageVersion;
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add red");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.Collection(
            promptedPackages!,
            p => Assert.Equal("Aspire.Hosting.Redis", p.Package.Id),
            p => Assert.Equal("Aspire.Hosting.Azure.Redis", p.Package.Id)
            );
        Assert.Equal("Aspire.Hosting.Redis", addedPackageName);
        Assert.Equal("9.2.0", addedPackageVersion);
    }

    [Fact]
    public async Task AddCommandPreservesSourceArgumentInBothCommands()
    {
        // Arrange
        string? addUsedSource = null;
        const string expectedSource = "https://custom-nuget-source.test/v3/index.json";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {

            // Makes it easier to isolate behavior in test case by disabling one
            // of the concurrent calls to the NuGetCache from the prefetcher.
            options.DisabledFeatures = [KnownFeatures.UpdateNotificationsEnabled];

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestAddCommandPrompter(interactionService);
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { redisPackage } //
                        );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    // Capture the source used for add
                    addUsedSource = nugetSource;

                    // Simulate adding the package.
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add redis --source {expectedSource}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal(expectedSource, addUsedSource);
    }

    [Fact]
    public async Task AddCommand_EmptyPackageList_DisplaysErrorMessage()
    {
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    return (0, Array.Empty<NuGetPackage>());
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.Contains(testInteractionService.DisplayedErrors, e => e.Contains(AddCommandStrings.NoIntegrationPackagesFound));
    }

    [Fact]
    public async Task AddCommand_NoMatchingPackages_DisplaysNoMatchesMessage()
    {
        string? displayedSubtleMessage = null;
        bool promptedForIntegration = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.InteractionServiceFactory = (sp) =>
            {
                var testInteractionService = new TestInteractionService();
                testInteractionService.DisplaySubtleMessageCallback = (message) =>
                {
                    displayedSubtleMessage = message;
                };
                return testInteractionService;
            };

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    return packages.First();
                };
                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var dockerPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Docker",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (0, new NuGetPackage[] { dockerPackage, redisPackage });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add nonexistentpackage");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        Assert.True(promptedForIntegration);
        Assert.Equal(string.Format(AddCommandStrings.NoPackagesMatchedSearchTerm, "nonexistentpackage"), displayedSubtleMessage);
    }

    [Theory]
    [InlineData("Aspire.Hosting.Azure.Redis", "azure-redis")]
    [InlineData("CommunityToolkit.Aspire.Hosting.Cosmos", "communitytoolkit-cosmos")]
    [InlineData("Aspire.Hosting.Postgres", "postgres")]
    [InlineData("Acme.Aspire.Hosting.Foo.Bar", "acme-foo-bar")]
    [InlineData("Aspire.Hosting.Docker", "docker")]
    [InlineData("SomeOther.Package.Name", "someother-package-name")]
    public void GenerateFriendlyName_ProducesExpectedResults(string packageId, string expectedFriendlyName)
    {
        // Arrange
        var package = new NuGetPackage { Id = packageId, Version = "1.0.0", Source = "test" };

        // Act
        var result = AddCommand.GenerateFriendlyName((package, null!)); // Null is OK for this test.

        // Assert
        Assert.Equal(expectedFriendlyName, result.FriendlyName);
        Assert.Equal(package, result.Package);
    }

    [Fact]
    public async Task AddCommandPrompter_FiltersToHighestVersionPerPackageId()
    {
        // Arrange
        List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? displayedPackages = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                var mockInteraction = new TestInteractionService();
                mockInteraction.PromptForSelectionCallback = (message, choices, formatter, ct) =>
                {
                    // Capture what the prompter passes to the interaction service
                    var choicesList = choices.Cast<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>().ToList();
                    displayedPackages = choicesList;
                    return choicesList.First();
                };
                return mockInteraction;
            };
        });
        using var provider = services.BuildServiceProvider();
        var interactionService = provider.GetRequiredService<IInteractionService>();

        var prompter = new AddCommandPrompter(interactionService);

        // Create a fake channel
        var fakeCache = new FakeNuGetPackageCache();
        var channel = PackageChannel.CreateImplicitChannel(fakeCache);

        // Create multiple versions of the same package
        var packages = new[]
        {
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.1.0", Source = "nuget" }, channel),
        };

        // Act
        await prompter.PromptForIntegrationAsync(packages, CancellationToken.None).DefaultTimeout();

        // Assert - should only show highest version (9.2.0) for the package ID
        Assert.NotNull(displayedPackages);
        Assert.Single(displayedPackages!);
        Assert.Equal("9.2.0", displayedPackages!.First().Package.Version);
    }

    [Fact]
    public async Task AddCommandPrompter_FiltersToHighestVersionPerChannel()
    {
        // Arrange
        List<object>? displayedChoices = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                var mockInteraction = new TestInteractionService();
                mockInteraction.PromptForSelectionCallback = (message, choices, formatter, ct) =>
                {
                    // Capture what the prompter passes to the interaction service
                    var choicesList = choices.Cast<object>().ToList();
                    displayedChoices = choicesList;
                    return choicesList.First();
                };
                return mockInteraction;
            };
        });
        using var provider = services.BuildServiceProvider();
        var interactionService = provider.GetRequiredService<IInteractionService>();

        var prompter = new AddCommandPrompter(interactionService);

        // Create a fake channel
        var fakeCache = new FakeNuGetPackageCache();
        var channel = PackageChannel.CreateImplicitChannel(fakeCache);

        // Create multiple versions of the same package from same channel
        var packages = new[]
        {
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.1.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.1-preview.1", Source = "nuget" }, channel),
        };

        // Act
        var result = await prompter.PromptForIntegrationVersionAsync(packages, CancellationToken.None).DefaultTimeout();

        // Assert - For implicit channel with no explicit channels, should automatically select highest version without prompting
        Assert.Null(displayedChoices); // No prompt should be shown
        Assert.Equal("9.2.0", result.Package.Version); // Should return highest version
    }

    [Fact]
    public async Task AddCommandPrompter_ShowsHighestVersionPerChannelWhenMultipleChannels()
    {
        // Arrange
        List<object>? displayedChoices = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                var mockInteraction = new TestInteractionService();
                mockInteraction.PromptForSelectionCallback = (message, choices, formatter, ct) =>
                {
                    // Capture what the prompter passes to the interaction service
                    var choicesList = choices.Cast<object>().ToList();
                    displayedChoices = choicesList;
                    return choicesList.First();
                };
                return mockInteraction;
            };
        });
        using var provider = services.BuildServiceProvider();
        var interactionService = provider.GetRequiredService<IInteractionService>();

        var prompter = new AddCommandPrompter(interactionService);

        // Create two different channels
        var fakeCache = new FakeNuGetPackageCache();
        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
        
        var mappings = new[] { new PackageMapping("Aspire*", "https://preview-feed") };
        var explicitChannel = PackageChannel.CreateExplicitChannel("preview", PackageChannelQuality.Prerelease, mappings, fakeCache);

        // Create packages from different channels with different versions
        var packages = new[]
        {
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.0", Source = "nuget" }, implicitChannel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.1.0", Source = "nuget" }, implicitChannel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }, implicitChannel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "10.0.0-preview.1", Source = "preview-feed" }, explicitChannel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "10.0.0-preview.2", Source = "preview-feed" }, explicitChannel),
        };

        // Act
        await prompter.PromptForIntegrationVersionAsync(packages, CancellationToken.None).DefaultTimeout();

        // Assert - should show 2 root choices: one for implicit channel, one submenu for explicit channel
        Assert.NotNull(displayedChoices);
        Assert.Equal(2, displayedChoices!.Count);
    }

    [Fact]
    public async Task AddCommand_WithoutHives_UsesImplicitChannelWithoutPrompting()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        var selectedPackageId = string.Empty;
        
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (message, choices, formatter, ct) =>
                {
                    return choices.Cast<object>().First();
                }
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (0, new NuGetPackage[] { redisPackage });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    selectedPackageId = packageName;
                    return 0;
                };

                return runner;
            };
        });
        
        using var provider = services.BuildServiceProvider();

        // Act - without hives, should automatically select from implicit channel without prompting
        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("Aspire.Hosting.Redis", selectedPackageId);
    }

    [Fact]
    public async Task AddCommand_WithHives_PrefersImplicitChannelVersionInNonInteractiveMode()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        hivesDir.Create();
        hivesDir.CreateSubdirectory("pr-12345");

        var selectedPackageVersion = string.Empty;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.InteractionServiceFactory = _ => new TestInteractionService()
            {
                PromptForSelectionCallback = (message, choices, formatter, ct) => choices.Cast<object>().First()
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    var implicitPackage = new NuGetPackage
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "implicit",
                        Version = "13.2.0-pr.12345.gabc"
                    };

                    var explicitPackage = new NuGetPackage
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "explicit",
                        Version = "13.3.0-preview.1.1"
                    };

                    return nugetSource is null
                        ? (0, new[] { implicitPackage })
                        : (0, new[] { explicitPackage });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("13.2.0-pr.12345.gabc", selectedPackageVersion);
    }

    [Fact]
    public async Task AddCommand_WithPrHive_PrefersCurrentCliVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        hivesDir.Create();
        hivesDir.CreateSubdirectory("pr-12345");

        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var selectedPackageVersion = string.Empty;
        var promptedForVersion = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not prompt when the current CLI version is available in a PR hive.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    var implicitPackage = new NuGetPackage
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "implicit",
                        Version = "13.2.2"
                    };

                    var prHivePackage = new NuGetPackage
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "pr-hive",
                        Version = cliVersion
                    };

                    return nugetSource is null
                        ? (0, new[] { implicitPackage })
                        : (0, new[] { prHivePackage });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add redis");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForVersion);
        Assert.Equal(cliVersion, selectedPackageVersion);
    }
}

internal sealed class TestAddCommandPrompter(IInteractionService interactionService) : AddCommandPrompter(interactionService)
{
    public Func<IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>, (string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? PromptForIntegrationCallback { get; set; }
    public Func<IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>, (string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? PromptForIntegrationVersionCallback { get; set; }

    public override Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        return PromptForIntegrationCallback switch
        {
            { } callback => Task.FromResult(callback(packages)),
            _ => Task.FromResult(packages.First()) // If no callback is provided just accept the first package.
        };
    }

    public override Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        return PromptForIntegrationVersionCallback switch
        {
            { } callback => Task.FromResult(callback(packages)),
            _ => Task.FromResult(packages.First()) // If no callback is provided just accept the first package.
        };
    }
}

public class AddCommandFuzzySearchTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AddCommand_WithStartsWith_FindsMatchUsingFuzzySearch()
    {
        var promptedPackages = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();
        var addedPackage = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedPackages.AddRange(packages);
                    return packages.First();
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var postgresPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.PostgreSQL",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var rabbitMQPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.RabbitMQ",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { postgresPackage, redisPackage, rabbitMQPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackage = packageName;
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        // Use "postgre" instead of "postgresql" - should still find it via fuzzy search
        var result = command.Parse("add postgre");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        // Verify that PostgreSQL package was added through fuzzy matching
        Assert.Equal("Aspire.Hosting.PostgreSQL", addedPackage);
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNonExactPackageName_FailsInsteadOfUsingFuzzySearch()
    {
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var postgresPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.PostgreSQL",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0,
                        new NuGetPackage[] { postgresPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add postgre --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.Contains(string.Format(AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, "postgre"), testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNoMatchingPackageName_FailsInNonInteractiveMode()
    {
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var postgresPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.PostgreSQL",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0,
                        new NuGetPackage[] { postgresPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add nonexistentpackage --version 9.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.Contains(string.Format(AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, "nonexistentpackage"), testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task AddCommand_WithPartialMatch_FiltersUsingFuzzySearch()
    {
        var promptedPackages = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);

                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedPackages.AddRange(packages);
                    return packages.First();
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var postgresPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.PostgreSQL",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var rabbitMQPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.RabbitMQ",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var mysqlPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.MySql",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { postgresPackage, redisPackage, rabbitMQPackage, mysqlPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        // Use "sql" - should match both PostgreSQL and MySql, but not Redis or RabbitMQ
        var result = command.Parse("add sql");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        // Should have prompted with packages that fuzzy match "sql"
        Assert.True(promptedPackages.Count > 0);
        Assert.Contains(promptedPackages, p => p.Package.Id.Contains("SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNonExactPackageName_Interactive_UsesFuzzySearch()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var addedPackageName = string.Empty;
        var addedPackageVersion = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    return packages.Single(package => package.Package.Id == "Aspire.Hosting.PostgreSQL");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.MySql", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.PostgreSQL" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.MySql" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.MySql", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addedPackageName = packageName;
                    addedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add sql --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.True(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.Equal("Aspire.Hosting.PostgreSQL", addedPackageName);
        Assert.Equal("13.2.0", addedPackageVersion);
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNonExactPackageName_Interactive_FailsWhenSelectedPackageDoesNotContainVersion()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    return packages.Single(package => package.Package.Id == "Aspire.Hosting.MySql");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.MySql", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.PostgreSQL" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.MySql" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.MySql", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add sql --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.True(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.False(addPackageWasCalled);
        Assert.Contains(testInteractionService.DisplayedErrors, error => error.Contains("13.2.0") && error.Contains("Aspire.Hosting.MySql"));
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNoMatches_Interactive_PromptsAllPackagesAndPreservesVersion()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var displayedSubtleMessage = string.Empty;
        var addedPackageName = string.Empty;
        var addedPackageVersion = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplaySubtleMessageCallback = message => displayedSubtleMessage = message
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    return packages.Single(package => package.Package.Id == "Aspire.Hosting.Redis");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.Docker" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addedPackageName = packageName;
                    addedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add nonexistentpackage --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.True(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.Equal(string.Format(AddCommandStrings.NoPackagesMatchedSearchTerm, "nonexistentpackage"), displayedSubtleMessage);
        Assert.Equal("Aspire.Hosting.Redis", addedPackageName);
        Assert.Equal("13.2.0", addedPackageVersion);
    }

    [Fact]
    public async Task AddCommand_WithVersionAndNoMatches_Interactive_FailsWhenSelectedPackageDoesNotContainVersion()
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var displayedSubtleMessage = string.Empty;
        var addPackageWasCalled = false;
        var testInteractionService = new TestInteractionService
        {
            DisplaySubtleMessageCallback = message => displayedSubtleMessage = message
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (packages) =>
                {
                    promptedForIntegration = true;
                    return packages.Single(package => package.Package.Id == "Aspire.Hosting.Docker");
                };
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not have been prompted for integration version.");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    if (!exactMatch)
                    {
                        return (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" }
                        ]);
                    }

                    return query switch
                    {
                        "Aspire.Hosting.Redis" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                            new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                        ]),
                        "Aspire.Hosting.Docker" => (0, [
                            new NuGetPackage { Id = "Aspire.Hosting.Docker", Source = "nuget", Version = "13.3.0" }
                        ]),
                        _ => (0, Array.Empty<NuGetPackage>())
                    };
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add nonexistentpackage --version 13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.True(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.False(addPackageWasCalled);
        Assert.Equal(string.Format(AddCommandStrings.NoPackagesMatchedSearchTerm, "nonexistentpackage"), displayedSubtleMessage);
        Assert.Contains(testInteractionService.DisplayedErrors, error => error.Contains("13.2.0") && error.Contains("Aspire.Hosting.Docker"));
    }

    [Fact]
    public async Task AddCommand_WithTypo_FindsMatchUsingFuzzySearch()
    {
        var addedPackage = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestAddCommandPrompter(interactionService);
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    var appContainersPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Azure.AppContainers",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var redisPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.Redis",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    var postgresPackage = new NuGetPackage()
                    {
                        Id = "Aspire.Hosting.PostgreSQL",
                        Source = "nuget",
                        Version = "9.2.0"
                    };

                    return (
                        0, // Exit code.
                        new NuGetPackage[] { appContainersPackage, redisPackage, postgresPackage }
                    );
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackage = packageName;
                    return 0; // Success.
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        // Use "azureapp" (Azure AppContainers) - should find Azure.AppContainers via fuzzy search
        var result = command.Parse("add azureapp");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
        // Verify that Azure AppContainers package was found and added through fuzzy matching
        Assert.Equal("Aspire.Hosting.Azure.AppContainers", addedPackage);
    }
}
