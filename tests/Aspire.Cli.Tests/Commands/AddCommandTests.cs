// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Microsoft.AspNetCore.InternalTesting;
using ExitCodeConstants = Aspire.Cli.CliExitCodes;

namespace Aspire.Cli.Tests.Commands;

public class AddCommandTests(ITestOutputHelper outputHelper)
{
    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

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
    public async Task IntegrationAddCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration add --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task IntegrationSearchCommandWithJsonOptionDoesNotEmitDiscoveryJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Should not search packages for the removed --json alias.");
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search redis --json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(rawJson);
    }

    [Fact]
    public async Task IntegrationSearchCommandRequiresQuery()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Should not search packages when the required search query is missing.");
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(rawJson);
    }

    [Fact]
    public async Task IntegrationListCommandFormatJsonReturnsAvailableIntegrationsWithoutPromptingOrAddingPackage()
    {
        var addPackageWasCalled = false;
        var projectLocatorWasCalled = false;
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                {
                    projectLocatorWasCalled = true;
                    return Task.FromResult(new AppHostProjectSearchResult(null, []));
                }
            };

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationCallback = (_) =>
                {
                    promptedForIntegration = true;
                    throw new InvalidOperationException("Should not prompt for integration when listing integrations.");
                };
                prompter.PromptForIntegrationVersionCallback = (_) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not prompt for version when listing integrations.");
                };
                return prompter;
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    return (0, new[]
                    {
                        CreatePackage("Aspire.Hosting.Docker", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.3.0"),
                        CreatePackage("Aspire.Hosting.Azure.Redis", "9.2.0")
                    });
                };
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(projectLocatorWasCalled);
        Assert.False(promptedForIntegration);
        Assert.False(promptedForVersion);
        Assert.False(addPackageWasCalled);

        var integrations = ReadIntegrationResults(rawJson);
        Assert.Equal(3, integrations.Length);
        Assert.Contains(integrations, i => i.Name == "azure-redis" && i.Package == "Aspire.Hosting.Azure.Redis" && i.Version == "9.2.0");
        Assert.Contains(integrations, i => i.Name == "docker" && i.Package == "Aspire.Hosting.Docker" && i.Version == "9.2.0");
        Assert.Contains(integrations, i => i.Name == "redis" && i.Package == "Aspire.Hosting.Redis" && i.Version == "9.3.0");
    }

    [Theory]
    [InlineData("integration list --format json")]
    [InlineData("integration search redis --format json")]
    public async Task IntegrationDiscoveryCommandFormatJsonReturnsEmptyArrayWhenNoIntegrationsAreAvailable(string commandLine)
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) => (0, []);
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(commandLine);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(ReadIntegrationResults(rawJson));
    }

    [Fact]
    public async Task IntegrationDiscoveryCommandReturnsSearchFailureExitCodeWhenPackageDiscoveryFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Search failed.");
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToSearchIntegrations, exitCode);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonFiltersAvailableIntegrationsWithoutAddingPackage()
    {
        var addPackageWasCalled = false;
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Should not locate an AppHost when searching integrations.")
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    return (0, new[]
                    {
                        CreatePackage("Aspire.Hosting.Docker", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Azure.Redis", "9.2.0")
                    });
                };
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search redis --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(addPackageWasCalled);

        var integrations = ReadIntegrationResults(rawJson);
        Assert.Equal(2, integrations.Length);
        Assert.Contains(integrations, i => i.Package == "Aspire.Hosting.Redis");
        Assert.Contains(integrations, i => i.Package == "Aspire.Hosting.Azure.Redis");
        Assert.DoesNotContain(integrations, i => i.Package == "Aspire.Hosting.Docker");
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonUsesFuzzyIntegrationMatching()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    return (0, new[]
                    {
                        CreatePackage("Aspire.Hosting.Docker", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0"),
                        CreatePackage("Aspire.Hosting.RabbitMQ", "9.2.0")
                    });
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search rdis --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var integrations = ReadIntegrationResults(rawJson);
        var integration = Assert.Single(integrations);
        Assert.Equal("redis", integration.Name);
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonWithTypeScriptAppHostPinnedToChannelAlsoSearchesImplicitChannel()
    {
        // Regression for https://github.com/microsoft/aspire/issues/17724 + https://github.com/microsoft/aspire/issues/17725.
        //
        // Layer 1 (latent bug, born 2026-01-13 in PR #13705): IntegrationPackageSearchService used to
        //   narrow the channel set to whatever `configuredChannel` resolved to whenever the apphost was
        //   non-C#. This dropped the implicit channel and any other channels from discovery.
        // Layer 2 (PR #17452, 2026-05-26): `aspire init` started writing `"channel": "<identity>"` into
        //   the scaffolded aspire.config.json for polyglot apphosts. This activated the Layer 1 bug for
        //   every newly-initialized TS apphost in 13.4.
        //
        // Fix: IntegrationPackageSearchService no longer narrows. The full channel set (implicit +
        //   pinned channel + any hives) is searched.
        //
        // This test pins the TS apphost to the "daily" channel via aspire.config.json. Pre-fix only the
        // daily channel was searched and Redis 2.0.0 (daily) was the only result. Post-fix the implicit
        // channel is ALSO searched, and SelectPreferredIntegrationPackage prefers the implicit channel
        // when versions collide on Id, so Redis 1.0.0 (implicit) wins the dedupe.
        //
        // The structural guarantee asserted below — both `implicitHits` AND `dailyHits` being > 0 — is
        // what defends against a regression that drops either channel from the search. Asserting only
        // on the resulting Redis version is insufficient because implicit-only and daily-only searches
        // both happen to produce a single result.
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "channel": "daily"
            }
            """);

        // Track per-channel invocation. IntegrationPackageSearchService walks channels via
        // Parallel.ForEachAsync, so callbacks may run concurrently; Interlocked guards that.
        var implicitHits = 0;
        var dailyHits = 0;
        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "1.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };
        var dailyCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref dailyHits);
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "2.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], dailyCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search redis --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // Structural regression signal: BOTH channels must have been searched.
        Assert.True(implicitHits > 0, "Implicit channel was not queried — discovery is dropping it.");
        Assert.True(dailyHits > 0, "Daily channel was not queried — pinned channel is being dropped from discovery.");

        // Implicit channel result wins the dedupe (SelectPreferredIntegrationPackage prefers implicit).
        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("1.0.0", integration.Version);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonWithTypeScriptAppHostPinnedToStagingChannelAlsoSearchesImplicitChannel()
    {
        // See companion test above for the full Layer 1 / Layer 2 regression story.
        // This variant covers the staging-channel pin: a stable-shaped CLI dogfooder whose apphost
        // was init'd by PR #17452 and now has `"channel": "staging"` written into aspire.config.json.
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "channel": "staging"
            }
            """);

        var implicitHits = 0;
        var stagingHits = 0;
        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "1.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true).ToArray());
            }
        };
        var stagingCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref stagingHits);
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "2.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Stable);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, PackageChannelQuality.Both, [new PackageMapping("Aspire*", "staging")], stagingCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search redis --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        Assert.True(implicitHits > 0, "Implicit channel was not queried — discovery is dropping it.");
        Assert.True(stagingHits > 0, "Staging channel was not queried — pinned channel is being dropped from discovery.");

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("1.0.0", integration.Version);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonWithTypeScriptAppHostPinnedToStableChannelStillSurfacesPrereleaseOnlyPackages()
    {
        // Regression for https://github.com/microsoft/aspire/issues/17725 specifically.
        //
        // Aspire.Hosting.Foundry has never shipped a stable version — it only exists as prerelease.
        // Pre-fix, a TS apphost with `"channel": "stable"` in aspire.config.json got narrowed to the
        // stable channel only. That channel is Quality.Stable, so only `prerelease: false` queries
        // were issued, and Foundry never appeared in the result set. Users dogfooding the staging CLI
        // (which writes `"channel": "stable"` for a stable-shaped build) could not discover Foundry.
        //
        // Post-fix the implicit channel (Quality.Both) is also searched, which DOES issue
        // `prerelease: true` queries, and Foundry surfaces.
        //
        // The fake here respects the `prerelease` arg passed to GetIntegrationPackagesAsync so the
        // stable channel sees Redis only, while the implicit channel sees Redis + Foundry. The
        // existence of Foundry in the result is the regression signal.
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "channel": "stable"
            }
            """);

        // Implicit channel: Quality.Both. Returns Redis when prerelease=false, Redis+Foundry when prerelease=true.
        var implicitHits = 0;
        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, prerelease, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                NuGetPackage[] packages = [];
                if (query == HostingIntegrationMetadata.DiscoveryQuery)
                {
                    packages = prerelease
                        ? [CreatePackage("Aspire.Hosting.Redis", "1.0.0"), CreatePackage("Aspire.Hosting.Foundry", "1.0.0-preview.1")]
                        : [CreatePackage("Aspire.Hosting.Redis", "1.0.0")];
                }

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };
        // Stable channel: Quality.Stable. PackageChannel only issues prerelease=false queries against it,
        // so Foundry (prerelease-only) never appears regardless of what the cache could return.
        var stableHits = 0;
        var stableCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref stableHits);
                NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? [CreatePackage("Aspire.Hosting.Redis", "1.0.0")]
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Stable);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, [new PackageMapping("Aspire*", "stable")], stableCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search foundry --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // Both channels must be queried. The implicit channel is what surfaces Foundry (via
        // prerelease=true), but the stable channel must also be searched so users who pinned to
        // it don't lose stable-only packages.
        Assert.True(implicitHits > 0, "Implicit channel was not queried — Foundry would not be discoverable.");
        Assert.True(stableHits > 0, "Stable channel was not queried — pinned channel is being dropped from discovery.");

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Foundry", integration.Package);
        Assert.Equal("1.0.0-preview.1", integration.Version);
    }

    [Theory]
    [InlineData(null, false)]          // No persisted channel — only implicit is searched, the explicit channel is excluded.
    [InlineData("\"daily\"", true)]    // Persisted daily channel — implicit AND daily are searched.
    [InlineData("\"staging\"", true)]  // Persisted staging channel — implicit AND staging are searched. Proves the gate is channel-name-opaque,
                                       // so the post-fix behavior verified for "daily" applies equally to a staging-stamped release where
                                       // `aspire new` would write `"channel": "staging"` into the polyglot apphost's aspire.config.json.
                                       // (See IntegrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync: the gate is
                                       //  `hasHives || !string.IsNullOrEmpty(configuredChannel)` — it never inspects the channel name.)
    public async Task IntegrationSearchCommandTypeScriptAppHostPersistedChannelExpandsDiscoveryWithoutChangingPreferredResult(string? configFileChannelJson, bool expectExplicitChannelHit)
    {
        // Durable regression guard against re-introducing the Layer-1 narrowing bug.
        //
        // Pre-fix: aspire.config.json with `"channel"` set caused IntegrationPackageSearchService to
        //   narrow the channel set to that single channel, so the with-channel arm would have returned
        //   ONLY the daily channel's Redis (2.0.0) while the without-channel arm returned Redis 1.0.0.
        // Post-fix two things hold simultaneously:
        //   (a) Both arms yield the SAME preferred Redis to the user (1.0.0, the implicit channel
        //       wins via SelectPreferredIntegrationPackage) — because the pin no longer overrides
        //       what the user sees as the top-ranked result.
        //   (b) The with-channel arm ALSO queries the pinned (daily) channel; the without-channel arm
        //       does not — because the explicit channel set is gated on `hasHives || !empty(configuredChannel)`.
        //
        // Both halves matter. (a) alone would pass for an implementation that incorrectly narrowed
        // to implicit-only when a channel was pinned (a different regression than the original bug
        // but still wrong — it would mean users who pin to `daily` lose access to packages that only
        // exist on the daily feed). (b) is the new structural guarantee on top of (a).
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        if (configFileChannelJson is not null)
        {
            File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), $$"""
                {
                  "channel": {{configFileChannelJson}}
                }
                """);
        }

        var implicitHits = 0;
        var dailyHits = 0;
        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? [CreatePackage("Aspire.Hosting.Redis", "1.0.0")]
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };
        var dailyCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                Interlocked.Increment(ref dailyHits);
                NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? [CreatePackage("Aspire.Hosting.Redis", "2.0.0")]
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], dailyCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search redis --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // (a) User-visible result is identical across arms: implicit Redis 1.0.0 wins.
        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("1.0.0", integration.Version);

        // (b) Per-channel search invocation differs based on whether a channel was pinned.
        Assert.True(implicitHits > 0, "Implicit channel must always be searched.");
        if (expectExplicitChannelHit)
        {
            // The explicit (daily) channel registered in the fake PackagingService gets searched
            // regardless of what channel NAME the apphost pinned (the gate is channel-name-opaque —
            // it only checks `!string.IsNullOrEmpty(configuredChannel)`). That's how a real CLI
            // built with `AspireCliChannel=staging` (writing `"channel": "staging"` into apphosts
            // via `aspire new`) will exercise the same gate path as a CLI that pinned `"daily"`.
            Assert.True(dailyHits > 0, $"With-channel arm: explicit channel must also be searched when apphost pin is non-empty (configured: {configFileChannelJson}).");
        }
        else
        {
            Assert.Equal(0, dailyHits);
        }
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonDiscoversTaggedThirdPartyPackagesWithoutUsingAspireConfigPackageTags()
    {
        var rawJson = string.Empty;
        var queriedPackages = new System.Collections.Concurrent.ConcurrentBag<string>();
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        const string source = "https://example.test/v3/index.json";
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "packageTags": {
                "Contoso.Legacy.Package": ["aspire-hosting"],
                "Contoso.Other.Package": ["aspire-hosting"]
              }
            }
            """);

        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                queriedPackages.Add(query);

                var packages = query switch
                {
                    var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => new[] { CreatePackage("Contoso.Hosting.MongoDb", "1.2.3") },
                    _ => Array.Empty<NuGetPackage>()
                };

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName]);
                return runner;
            };
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(cache)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": "database aspire-hosting aspire"
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search mongodb --apphost \"{appHostFile.FullName}\" --format json --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains(HostingIntegrationMetadata.DiscoveryQuery, queriedPackages);
        Assert.DoesNotContain("Contoso.Legacy.Package", queriedPackages);
        Assert.DoesNotContain("Contoso.Other.Package", queriedPackages);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Contoso.Hosting.MongoDb", integration.Package);
        Assert.Equal("1.2.3", integration.Version);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonWithAppHostOutsideLaunchDirectoryUsesConfiguredStagingChannelWithRealPackagingService()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "elsewhere"));
        var appHostFile = new FileInfo(Path.Combine(projectDirectory.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName), """
            {
              "channel": "staging"
            }
            """);

        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "2.0.0") }.Where(package => filter?.Invoke(package.Id) ?? true).ToArray()
                    : Array.Empty<NuGetPackage>())
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Stable);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.NuGetPackageCacheFactory = _ => cache;
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search redis --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("2.0.0", integration.Version);
    }

    [Fact]
    public async Task IntegrationListCommandFormatJsonUsesTagSearchAndExcludesKnownNonHostingAspirePackages()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        const string source = "https://example.test/v3/index.json";
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query switch
                {
                    var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => new[]
                    {
                        CreatePackage("Contoso.Hosting.MongoDb", "1.2.3"),
                        CreatePackage("Aspire.StackExchange.Redis", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Dapr", "9.1.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0")
                    },
                    _ => Array.Empty<NuGetPackage>()
                };

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName]);
                return runner;
            };
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(cache)
                ])
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": "database aspire-hosting aspire"
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.other.package/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "2.0.0",
                            "tags": "database"
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var integrations = ReadIntegrationResults(rawJson);
        Assert.Equal(2, integrations.Length);
        Assert.Contains(integrations, i => i.Package == "Aspire.Hosting.Redis");
        Assert.Contains(integrations, i => i.Package == "Contoso.Hosting.MongoDb" && i.Version == "1.2.3");
        Assert.DoesNotContain(integrations, i => i.Package == "Aspire.StackExchange.Redis");
        Assert.DoesNotContain(integrations, i => i.Package == "Aspire.Hosting.Dapr");
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonWithUnpinnedAppHostUsesImplicitChannelUnderStagingCli()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);

        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "1.0.0") }.Where(package => filter?.Invoke(package.Id) ?? true).ToArray()
                    : Array.Empty<NuGetPackage>())
        };
        var stagingCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "2.0.0") }.Where(package => filter?.Invoke(package.Id) ?? true).ToArray()
                    : Array.Empty<NuGetPackage>())
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Staging);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, PackageChannelQuality.Both, [new PackageMapping("Aspire*", "staging")], stagingCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search redis --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("1.0.0", integration.Version);
    }

    [Fact]
    public async Task IntegrationListCommandFormatJsonDefaultsToOfficialPackages()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[]
                    {
                        CreatePackage("Contoso.Hosting.MongoDb", "1.2.3"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0")
                    }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
    }

    [Fact]
    public async Task IntegrationListCommandFormatJsonUsesConfiguredThirdPartyFeedsAndPackageAllowlist()
    {
        const string configuredFeed = "https://example.test/v3/index.json";

        var rawJson = string.Empty;
        string? generatedNuGetConfig = null;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), $$"""
            {
              "integrations": {
                "discovery": {
                  "thirdParty": {
                    "mode": "on",
                    "feeds": ["{{configuredFeed}}"],
                    "packages": ["Contoso.Hosting.MongoDb"]
                  }
                }
              }
            }
            """);

        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, nugetConfigFile, _, _) =>
            {
                if (nugetConfigFile is not null)
                {
                    generatedNuGetConfig = File.ReadAllText(nugetConfigFile.FullName);
                }

                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[]
                    {
                        CreatePackage("Contoso.Hosting.MongoDb", "1.2.3"),
                        CreatePackage("Fabrikam.Hosting.Postgres", "2.0.0")
                    }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.NuGetPackageCacheFactory = _ => cache;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(generatedNuGetConfig);
        Assert.Contains(configuredFeed, generatedNuGetConfig);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Contoso.Hosting.MongoDb", integration.Package);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonSearchesImplicitSourcesAndNuGetOrgStableChannel()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var privateFeedCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Contoso.Hosting.MongoDb", "1.2.3") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };
        var nuGetOrgCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Scalar.Aspire", "0.9.34") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Should not locate an AppHost when searching integrations.")
            };
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(privateFeedCache),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, [new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")], nuGetOrgCache)
                ])
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search scalar --format json --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Scalar.Aspire", integration.Package);
        Assert.Equal("0.9.34", integration.Version);
    }

    [Fact]
    public async Task IntegrationSearchCommandStagingStampedCliWithPinnedStagingApphostQueriesBothImplicitAndStagingChannelsAndSurfacesPrereleaseOnlyPackages()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "channel": "staging"
            }
            """);

        var totalCacheCalls = 0;
        var prereleaseRequested = 0;
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, prerelease, _, _, _) =>
            {
                Interlocked.Increment(ref totalCacheCalls);
                if (query == HostingIntegrationMetadata.DiscoveryQuery && prerelease)
                {
                    Interlocked.Increment(ref prereleaseRequested);
                    var packages = new[] { CreatePackage("Aspire.Hosting.Foundry", "13.4.0-rc.1") };
                    return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
                }

                return Task.FromResult<IEnumerable<NuGetPackage>>([]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Staging);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.NuGetPackageCacheFactory = _ => cache;
        });
        services.AddSingleton<IAppHostProjectFactory>(new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"integration search foundry --apphost \"{appHostFile.FullName}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Foundry", integration.Package);
        Assert.Equal("13.4.0-rc.1", integration.Version);
        Assert.True(totalCacheCalls >= 2, $"Expected >= 2 cache calls (both implicit and staging channels), got {totalCacheCalls}. Pre-fix narrowing would have produced 1 call.");
        Assert.True(prereleaseRequested >= 1, $"Expected at least one channel to request prerelease=true (Quality.Both channels do); got {prereleaseRequested}.");
    }

    [Fact]
    public async Task IntegrationListCommandFormatJsonPrefersImplicitChannelWhenMultipleChannelsContainSameIntegration()
    {
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "test-hive"));

        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "1.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };
        var explicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? new[] { CreatePackage("Aspire.Hosting.Redis", "2.0.0") }
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(p => filter(p.Id)).ToArray());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel("test-hive", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "test-hive")], explicitCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Redis", integration.Package);
        Assert.Equal("1.0.0", integration.Version);
    }

    [Fact]
    public async Task IntegrationSearchCommandFormatJsonReturnsEmptyArrayWhenNoIntegrationsMatch()
    {
        var addPackageWasCalled = false;
        var rawJson = string.Empty;
        var testInteractionService = new TestInteractionService
        {
            DisplayRawTextCallback = text => rawJson = text
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Should not locate an AppHost when searching integrations.")
            };

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    return (0, new[]
                    {
                        CreatePackage("Aspire.Hosting.Docker", "9.2.0"),
                        CreatePackage("Aspire.Hosting.Redis", "9.2.0")
                    });
                };
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration search azure --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(addPackageWasCalled);

        var integrations = ReadIntegrationResults(rawJson);
        Assert.Empty(integrations);
    }

    [Fact]
    public async Task AddCommandInteractiveFlowSmokeTest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

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
        var result = command.Parse("add --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AddCommandWithoutIntegrationNamePromptsEvenWhenSinglePackageFound()
    {
        var promptedForIntegration = false;
        var promptedPackageCount = 0;
        var promptedForVersion = false;
        var promptedVersionCount = 0;
        string? addedPackage = null;
        string? addUsedSource = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

            options.AddCommandPrompterFactory = sp =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService)
                {
                    PromptForIntegrationCallback = packages =>
                    {
                        var packageArray = packages.ToArray();
                        promptedForIntegration = true;
                        promptedPackageCount = packageArray.Length;

                        return packageArray.Single();
                    }
                };

                prompter.PromptForIntegrationVersionCallback = packages =>
                {
                    var packageArray = packages.ToArray();
                    promptedForVersion = true;
                    promptedVersionCount = packageArray.Length;

                    return packageArray.First();
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    return (0, new[] { CreatePackage("Aspire.Hosting.Redis", "1.0.1") });
                };

                runner.AddPackageAsyncCallback = (_, packageName, _, nugetSource, _, _, _) =>
                {
                    addedPackage = packageName;
                    addUsedSource = nugetSource;

                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.True(promptedForIntegration);
        Assert.Equal(1, promptedPackageCount);
        Assert.True(promptedForVersion);
        Assert.True(promptedVersionCount > 0);
        Assert.Equal("Aspire.Hosting.Redis", addedPackage);
        Assert.Null(addUsedSource);
    }

    [Fact]
    public async Task AddCommandWithoutIntegrationNameDoesNotPromptForInstalledPackages()
    {
        string? addedPackage = null;
        List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? promptedPackages = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = sp =>
            {
                var prompter = new TestAddCommandPrompter(sp.GetRequiredService<IInteractionService>())
                {
                    PromptForIntegrationCallback = packages =>
                    {
                        promptedPackages = packages.ToList();
                        return promptedPackages.Single();
                    },
                    PromptForIntegrationVersionCallback = packages => packages.Single()
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? new[]
                            {
                                CreatePackage("Aspire.Hosting.Redis", "9.2.0"),
                                CreatePackage("Aspire.Hosting.Docker", "9.2.0")
                            }
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => (0, JsonDocument.Parse("""
                    {
                      "Items": {
                        "PackageReference": [
                          { "Identity": "Aspire.Hosting.Redis", "Version": "9.2.0" }
                        ]
                      },
                      "Properties": {}
                    }
                    """)),
                AddPackageAsyncCallback = (_, packageName, _, _, _, _, _) =>
                {
                    addedPackage = packageName;
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(promptedPackages);
        var promptedPackage = Assert.Single(promptedPackages);
        Assert.Equal("Aspire.Hosting.Docker", promptedPackage.Package.Id);
        Assert.Equal("Aspire.Hosting.Docker", addedPackage);
    }

    [Fact]
    public async Task AddCommandWithoutIntegrationNameSortsPackagesByFriendlyName()
    {
        List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? promptedPackages = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = sp =>
            {
                var prompter = new TestAddCommandPrompter(sp.GetRequiredService<IInteractionService>())
                {
                    PromptForIntegrationCallback = packages =>
                    {
                        promptedPackages = packages.ToList();
                        return promptedPackages.First();
                    },
                    PromptForIntegrationVersionCallback = packages => packages.Single()
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? new[]
                            {
                                CreatePackage("Aspire.Hosting.Zookeeper", "9.2.0"),
                                CreatePackage("CommunityToolkit.Aspire.Hosting.Cosmos", "9.2.0")
                            }
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => (0, JsonDocument.Parse("""
                    {
                      "Items": {
                        "PackageReference": []
                      },
                      "Properties": {}
                    }
                    """)),
                AddPackageAsyncCallback = (_, _, _, _, _, _, _) => 0
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(promptedPackages);
        Assert.Collection(
            promptedPackages,
            package => Assert.Equal("communitytoolkit-cosmos", package.FriendlyName),
            package => Assert.Equal("zookeeper", package.FriendlyName));
    }

    [Fact]
    public async Task AddCommandPrompterSelectsSingleVersionWithoutPrompting()
    {
        var promptedForVersion = false;
        var testInteractionService = new TestInteractionService
        {
            PromptForSelectionCallback = (promptText, choices, formatter, _) =>
            {
                promptedForVersion = true;
                throw new InvalidOperationException("Version selection should not be prompted when there is only one version.");
            }
        };
        var prompter = new AddCommandPrompter(testInteractionService);
        var package = (
            FriendlyName: "redis",
            Package: CreatePackage("Aspire.Hosting.Redis", "1.0.1"),
            Channel: PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache()));

        var selectedPackage = await prompter.PromptForIntegrationVersionAsync([package], configuredChannel: null, CancellationToken.None);

        Assert.False(promptedForVersion);
        Assert.Equal("1.0.1", selectedPackage.Package.Version);
    }

    [Fact]
    public async Task AddCommandWithAskModeCanIncludeThirdPartyPackagesInteractively()
    {
        string? addedPackage = null;
        var scopePromptShown = false;
        var confirmationPromptShown = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
                var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
                await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "integrations": {
                "discovery": {
                  "thirdParty": { "mode": "ask" }
                }
              }
            }
            """);

        var testInteractionService = new TestInteractionService
        {
            PromptForSelectionCallback = (promptText, choices, formatter, _) =>
            {
                if (promptText == AddCommandStrings.SelectIntegrationDiscoveryScope)
                {
                    scopePromptShown = true;
                    return choices.Cast<object>().Single(choice => formatter(choice) == AddCommandStrings.DiscoveryScopeIncludeThirdParty);
                }

                if (promptText == string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ThirdPartyIntegrationConfirmationPrompt, "Contoso.Hosting.MongoDb"))
                {
                    confirmationPromptShown = true;
                    return choices.Cast<object>().Single(choice => formatter(choice) == AddCommandStrings.ThirdPartyIntegrationConfirmationYes);
                }

                return choices.Cast<object>().First();
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = sp =>
            {
                var prompter = new TestAddCommandPrompter(sp.GetRequiredService<IInteractionService>())
                {
                    PromptForIntegrationCallback = packages => packages.Single(package => package.Package.Id == "Contoso.Hosting.MongoDb")
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? new[]
                            {
                                CreatePackage("Contoso.Hosting.MongoDb", "1.2.3"),
                                CreatePackage("Aspire.Hosting.Redis", "9.2.0")
                            }
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, packageName, _, _, _, _, _) =>
                {
                    addedPackage = packageName;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
    var result = command.Parse($"add --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(scopePromptShown);
        Assert.True(confirmationPromptShown);
        Assert.Equal(1, testInteractionService.DisplayEmptyLineCount);
        Assert.Equal("Contoso.Hosting.MongoDb", addedPackage);
        Assert.DoesNotContain(AddCommandStrings.ThirdPartyIntegrationDeclined, testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task AddCommandWithThirdPartyPackageDoesNotAddWhenConfirmationIsDeclined()
    {
        var addPackageWasCalled = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var testInteractionService = new TestInteractionService
        {
            PromptForSelectionCallback = (promptText, choices, formatter, _) =>
            {
                if (promptText == string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ThirdPartyIntegrationConfirmationPrompt, "Contoso.Hosting.MongoDb"))
                {
                    return choices.Cast<object>().Single(choice => formatter(choice) == AddCommandStrings.ThirdPartyIntegrationConfirmationNo);
                }

                return choices.Cast<object>().First();
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AddCommandPrompterFactory = sp =>
            {
                var prompter = new TestAddCommandPrompter(sp.GetRequiredService<IInteractionService>())
                {
                    PromptForIntegrationCallback = packages => packages.Single(package => package.Package.Id == "Contoso.Hosting.MongoDb")
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        var packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? new[] { CreatePackage("Contoso.Hosting.MongoDb", "1.2.3") }
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add --apphost \"{appHostFile.FullName}\" --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.Contains(AddCommandStrings.ThirdPartyIntegrationDeclined, testInteractionService.DisplayedErrors);
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

    [Theory]
    [InlineData("redis")]
    [InlineData("Aspire.Hosting.Redis")]
    public async Task AddCommandInteractiveDoesNotPromptForIntegrationWhenExactMatchIsFound(string integrationName)
    {
        var promptedForIntegration = false;
        var promptedForVersion = false;
        var selectedPackageName = string.Empty;
        var selectedPackageVersion = string.Empty;

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
                    return packages.Single(package => package.Package.Version == "13.2.0");
                };

                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    return (0, [
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.3.0" },
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "13.2.0" }
                    ]);
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, invocationOptions, cancellationToken) =>
                {
                    selectedPackageName = packageName;
                    selectedPackageVersion = packageVersion;
                    return 0;
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add {integrationName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(promptedForIntegration);
        Assert.True(promptedForVersion);
        Assert.Equal("Aspire.Hosting.Redis", selectedPackageName);
        Assert.Equal("13.2.0", selectedPackageVersion);
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
    public async Task AddCommandExactPackageIdSearchUsesTaggedMetadataWithoutCreatingSettingsState()
    {
        const string packageId = "Contoso.Aspire.Hosting.MongoDb";
        const string packageVersion = "1.2.3";
        const string source = "https://example.test/v3/index.json";

        string? addedPackageId = null;
        string? addedPackageVersion = null;
        bool? createSettingsFile = null;
        var testInteractionService = new TestInteractionService
        {
            PromptForSelectionCallback = (promptText, choices, formatter, _) =>
            {
                if (promptText == string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ThirdPartyIntegrationConfirmationPrompt, packageId))
                {
                    return choices.Cast<object>().Single(choice => formatter(choice) == AddCommandStrings.ThirdPartyIntegrationConfirmationYes);
                }

                return choices.Cast<object>().First();
            }
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        File.WriteAllText(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, requestedCreateSettingsFile, _) =>
                {
                    createSettingsFile = requestedCreateSettingsFile;
                    return Task.FromResult(new AppHostProjectSearchResult(projectFile ?? appHostFile, [projectFile ?? appHostFile]));
                }
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, queriedPackageId, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(queriedPackageId switch
                    {
                        var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => [CreatePackage("Aspire.Hosting.Redis", "9.2.0")],
                        packageId => [
                            CreatePackage(packageId, "1.3.0"),
                            CreatePackage(packageId, packageVersion)
                        ],
                        _ => []
                    })
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName]);
                runner.AddPackageAsyncCallback = (_, packageName, version, _, _, _, _) =>
                {
                    addedPackageId = packageName;
                    addedPackageVersion = version;
                    return 0;
                };

                return runner;
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.aspire.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.3.0",
                            "tags": "database aspire-hosting aspire",
                            "dependencyGroups": [
                              {
                                "targetFramework": "net10.0",
                                "dependencies": [
                                  { "id": "Aspire.Hosting.AppHost", "range": "[9.0.0, )" }
                                ]
                              }
                            ]
                          }
                        },
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": "database aspire-hosting aspire",
                            "dependencyGroups": [
                              {
                                "targetFramework": "net10.0",
                                "dependencies": [
                                  { "id": "Aspire.Hosting.AppHost", "range": "[9.0.0, )" }
                                ]
                              }
                            ]
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add {packageId} --version {packageVersion} --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal(packageId, addedPackageId);
        Assert.Equal(packageVersion, addedPackageVersion);
        Assert.NotNull(createSettingsFile);
        Assert.False(createSettingsFile.Value);
    }

    [Fact]
    public async Task ExactPackageIdSearchAcceptsThirdPartyPackageWithHostingDependencyMetadata()
    {
        const string packageId = "Contoso.Aspire.Hosting.MongoDb";
        const string packageVersion = "1.2.3";
        const string source = "https://example.test/v3/index.json";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, queriedPackageId, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(queriedPackageId switch
                    {
                        packageId => [CreatePackage(packageId, packageVersion)],
                        _ => []
                    })
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName])
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.aspire.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.2.3",
                        "tags": "database",
                        "dependencyGroups": [
                          {
                            "targetFramework": "net10.0",
                            "dependencies": [
                              { "id": "Aspire.Hosting.AppHost", "range": "[9.0.0, )" }
                            ]
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var searchService = provider.GetRequiredService<IntegrationPackageSearchService>();

        var matches = (await searchService.GetPackagesByExactIdWithChannelsAsync(
            workspace.WorkspaceRoot,
            packageId,
            configuredChannel: null,
            IntegrationDiscoveryScope.All,
            CancellationToken.None)).ToArray();

        var match = Assert.Single(matches);
        Assert.Equal(packageId, match.Package.Id);
        Assert.Equal(packageVersion, match.Package.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactPackageIdSearchRejectsThirdPartyPackageWithoutHostingDependencyMetadata(bool includeHostingTag)
    {
        const string packageId = "Contoso.Aspire.Hosting.MongoDb";
        const string packageVersion = "1.2.3";
        const string source = "https://example.test/v3/index.json";

        var tags = includeHostingTag ? $"database {HostingIntegrationMetadata.CanonicalTag}" : "database";
        const string dependencyGroups = "[]";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, queriedPackageId, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(queriedPackageId switch
                    {
                        packageId => [CreatePackage(packageId, packageVersion)],
                        _ => []
                    })
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName])
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.aspire.hosting.mongodb/index.json" => CreateJsonResponse($$"""
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.2.3",
                        "tags": "{{tags}}",
                        "dependencyGroups": {{dependencyGroups}}
                      }
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var searchService = provider.GetRequiredService<IntegrationPackageSearchService>();

        var matches = (await searchService.GetPackagesByExactIdWithChannelsAsync(
            workspace.WorkspaceRoot,
            packageId,
            configuredChannel: null,
            IntegrationDiscoveryScope.All,
            CancellationToken.None)).ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public async Task AddCommandExactPackageIdSearchUsesRequestedSourceForDependencyMetadata()
    {
        const string packageId = "Contoso.Aspire.Hosting.MongoDb";
        const string packageVersion = "1.2.3";
        const string source = "https://example.test/v3/index.json";

        string? addedPackageId = null;
        string? addedPackageVersion = null;
        string? addedPackageSource = null;
        var testInteractionService = new TestInteractionService
        {
            PromptForSelectionCallback = (promptText, choices, formatter, _) =>
            {
                var confirmationChoice = choices.Cast<object>().FirstOrDefault(choice => formatter(choice) == AddCommandStrings.ThirdPartyIntegrationConfirmationYes);
                if (confirmationChoice is not null)
                {
                    return confirmationChoice;
                }

                return choices.Cast<object>().First();
            }
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, queriedPackageId, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(queriedPackageId switch
            {
                packageId => [CreatePackage(packageId, packageVersion)],
                _ => []
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DisabledFeatures = [KnownFeatures.UpdateNotificationsEnabled];
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.NuGetPackageCacheFactory = _ => cache;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(projectFile ?? appHostFile, [projectFile ?? appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner
                {
                    GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [])
                };
                runner.AddPackageAsyncCallback = (_, packageName, version, nugetSource, _, _, _) =>
                {
                    addedPackageId = packageName;
                    addedPackageVersion = version;
                    addedPackageSource = nugetSource;
                    return 0;
                };

                return runner;
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.aspire.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "catalogEntry": {
                        "version": "1.2.3",
                        "tags": "database",
                        "dependencyGroups": [
                          {
                            "targetFramework": "net10.0",
                            "dependencies": [
                              { "id": "Aspire.Hosting.AppHost", "range": "[9.0.0, )" }
                            ]
                          }
                        ]
                      }
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add {packageId} --version {packageVersion} --apphost \"{appHostFile.FullName}\" --source {source}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == ExitCodeConstants.Success, string.Join(Environment.NewLine, testInteractionService.DisplayedErrors));
        Assert.Equal(packageId, addedPackageId);
        Assert.Equal(packageVersion, addedPackageVersion);
        Assert.Equal(source, addedPackageSource);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task AddCommandExactPackageIdSearchRejectsUnverifiedThirdPartyPackage()
    {
        const string packageId = "Contoso.Aspire.Hosting.MongoDb";
        const string packageVersion = "1.2.3";
        const string source = "https://example.test/v3/index.json";

        bool addPackageWasCalled = false;
        bool? createSettingsFile = null;
        var testInteractionService = new TestInteractionService();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var nugetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        File.WriteAllText(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(nugetConfigFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="test" value="{{source}}" />
              </packageSources>
            </configuration>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, requestedCreateSettingsFile, _) =>
                {
                    createSettingsFile = requestedCreateSettingsFile;
                    return Task.FromResult(new AppHostProjectSearchResult(projectFile ?? appHostFile, [projectFile ?? appHostFile]));
                }
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, queriedPackageId, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(queriedPackageId switch
                    {
                        var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => [CreatePackage("Aspire.Hosting.Redis", "9.2.0")],
                        packageId => [
                            CreatePackage(packageId, "1.3.0"),
                            CreatePackage(packageId, packageVersion)
                        ],
                        _ => []
                    })
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetConfigFile.FullName]);
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var handler = new MockHttpMessageHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            source => CreateJsonResponse("""
                {
                  "resources": [
                    {
                      "@id": "https://example.test/v3/registration-semver2/",
                      "@type": "RegistrationsBaseUrl/Versioned"
                    }
                  ]
                }
                """),
            "https://example.test/v3/registration-semver2/contoso.aspire.hosting.mongodb/index.json" => CreateJsonResponse("""
                {
                  "items": [
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.3.0",
                            "tags": "database"
                          }
                        },
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": "database"
                          }
                        }
                      ]
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add {packageId} --version {packageVersion} --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.NotNull(createSettingsFile);
        Assert.False(createSettingsFile.Value);
        Assert.Contains(
            string.Format(AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, packageId),
            testInteractionService.DisplayedErrors);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

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

            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
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

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.False(File.Exists(nugetConfigPath));
    }

    [Fact]
    public async Task AddCommandWithMissingLocalSourceDisplaysErrorBeforePackageSearch()
    {
        var testInteractionService = new TestInteractionService();
        var packageSearchWasCalled = false;
        var addPackageWasCalled = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var missingSource = Path.Combine(workspace.WorkspaceRoot.FullName, "missing-feed");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) =>
                {
                    packageSearchWasCalled = true;
                    return (0, Array.Empty<NuGetPackage>());
                };
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add redis --apphost \"{appHostFile.FullName}\" --source \"{missingSource}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(packageSearchWasCalled);
        Assert.False(addPackageWasCalled);
        Assert.Contains(
            string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SourceDoesNotExist, missingSource),
            testInteractionService.DisplayedErrors);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task AddCommandWithoutIntegrationNameInNonInteractiveModeDoesNotAddFirstPackage()
    {
        var testInteractionService = new TestInteractionService();
        var addPackageWasCalled = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                        query == HostingIntegrationMetadata.DiscoveryQuery
                            ? [
                                new NuGetPackage { Id = "AspireQuartz.Hosting", Version = "1.0.1", Source = "nuget" },
                                new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }
                            ]
                            : [])
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add --apphost \"{appHostFile.FullName}\" --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.Contains(AddCommandStrings.IntegrationNameRequiredInNonInteractiveMode, testInteractionService.DisplayedErrors);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task AddCommandFriendlyNameSearchFallsBackToBuiltInPackageIdAndCreatesPrHiveNuGetConfig()
    {
        const string nugetOrgSource = "https://api.nuget.org/v3/index.json";

        string? addedPackageId = null;
        string? addedPackageVersion = null;
        string? addUsedSource = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var prHiveSource = Path.Combine(workspace.WorkspaceRoot.FullName, "pr-hive", "packages");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "pr-16882"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        NuGetPackage[] packages = query switch
                        {
                            var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => [],
                            "Aspire.Hosting.mongodb" => [
                                new NuGetPackage { Id = "Aspire.Hosting.MongoDB", Version = "13.4.0-pr.16882.gf2644312", Source = prHiveSource }
                            ],
                            _ => []
                        };

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true));
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                        [
                            PackageChannel.CreateExplicitChannel(
                                "pr-16882",
                                PackageChannelQuality.Both,
                                [
                                    new PackageMapping("Aspire*", prHiveSource),
                                    new PackageMapping(PackageMapping.AllPackages, nugetOrgSource)
                                ],
                                cache,
                                pinnedVersion: "13.4.0-pr.16882.gf2644312")
                        ])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, packageName, version, nugetSource, _, _, _) =>
                {
                    addedPackageId = packageName;
                    addedPackageVersion = version;
                    addUsedSource = nugetSource;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add mongodb --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("Aspire.Hosting.MongoDB", addedPackageId);
        Assert.Equal("13.4.0-pr.16882.gf2644312", addedPackageVersion);
        Assert.Null(addUsedSource);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath));

        var nugetConfig = System.Xml.Linq.XDocument.Load(nugetConfigPath);
        var source = Assert.Single(nugetConfig.Descendants("add"));
        Assert.Equal(prHiveSource, source.Attribute("key")?.Value);
        Assert.Equal(prHiveSource, source.Attribute("value")?.Value);
    }

    [Fact]
    public async Task AddCommandInteractiveListIncludesBuiltInPackagesFromPrHiveWhenTagSearchDoesNotFindThem()
    {
        const string nugetOrgSource = "https://api.nuget.org/v3/index.json";

        List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>? promptedPackages = null;
        string? addedPackageId = null;
        string? addUsedSource = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var prHiveSource = Path.Combine(workspace.WorkspaceRoot.FullName, "pr-hive", "packages");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "pr-16882"));
        Directory.CreateDirectory(prHiveSource);
        await File.WriteAllTextAsync(Path.Combine(prHiveSource, "Aspire.Hosting.13.4.0-pr.16882.gf2644312.nupkg"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(prHiveSource, "Aspire.Hosting.MongoDB.13.4.0-pr.16882.gf2644312.nupkg"), string.Empty);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = sp =>
            {
                var prompter = new TestAddCommandPrompter(sp.GetRequiredService<IInteractionService>());
                prompter.PromptForIntegrationCallback = packages =>
                {
                    promptedPackages = packages.ToList();
                    var promptedPackageSummary = string.Join(", ", promptedPackages.Select(package => $"{package.Package.Id}@{package.Package.Version}[{package.Channel.Name}]"));
                    Assert.True(promptedPackages.Any(package => package.Package.Id == "Aspire.Hosting.MongoDB"), promptedPackageSummary);
                    return promptedPackages.Single(package => package.Package.Id == "Aspire.Hosting.MongoDB");
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        NuGetPackage[] packages = query switch
                        {
                            var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => [
                                new NuGetPackage { Id = "AspireQuartz.Hosting", Version = "1.0.1", Source = nugetOrgSource }
                            ],
                            _ => []
                        };

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true));
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                        [
                            PackageChannel.CreateExplicitChannel(
                                "pr-16882",
                                PackageChannelQuality.Both,
                                [
                                    new PackageMapping("Aspire*", prHiveSource),
                                    new PackageMapping(PackageMapping.AllPackages, nugetOrgSource)
                                ],
                                cache,
                                pinnedVersion: "13.4.0-pr.16882.gf2644312")
                        ])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, packageName, _, nugetSource, _, _, _) =>
                {
                    addedPackageId = packageName;
                    addUsedSource = nugetSource;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add --apphost \"{appHostFile.FullName}\" --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(promptedPackages);
        var promptedPackageSummary = string.Join(", ", promptedPackages.Select(package => $"{package.Package.Id}@{package.Package.Version}[{package.Channel.Name}]"));
        Assert.True(promptedPackages.Any(package => package.Package.Id == "AspireQuartz.Hosting"), promptedPackageSummary);
        Assert.True(promptedPackages.Any(package => package.Package.Id == "Aspire.Hosting.MongoDB"), promptedPackageSummary);
        Assert.Equal("Aspire.Hosting.MongoDB", addedPackageId);
        Assert.Null(addUsedSource);
    }

    [Fact]
    public async Task AddCommandPrHiveNuGetConfigCreationRespectsExistingConfigNameCasing()
    {
        const string nugetOrgSource = "https://api.nuget.org/v3/index.json";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var existingNuGetConfig = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(existingNuGetConfig, "<configuration />");
        var prHiveSource = Path.Combine(workspace.WorkspaceRoot.FullName, "pr-hive", "packages");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", "pr-16882"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        NuGetPackage[] packages = query switch
                        {
                            var tagQuery when tagQuery == HostingIntegrationMetadata.DiscoveryQuery => [],
                            "Aspire.Hosting.mongodb" => [
                                new NuGetPackage { Id = "Aspire.Hosting.MongoDB", Version = "13.4.0-pr.16882.gf2644312", Source = prHiveSource }
                            ],
                            _ => []
                        };

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true));
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                        [
                            PackageChannel.CreateExplicitChannel(
                                "pr-16882",
                                PackageChannelQuality.Both,
                                [
                                    new PackageMapping("Aspire*", prHiveSource),
                                    new PackageMapping(PackageMapping.AllPackages, nugetOrgSource)
                                ],
                                cache)
                        ])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner
                {
                    AddPackageAsyncCallback = (_, _, _, _, _, _, _) => 0
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add mongodb --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(existingNuGetConfig));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.WorkspaceRoot.FullName),
            file => string.Equals(Path.GetFileName(file), "nuget.config", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddCommandUsesMatchingMappingSourceWhenPackageSourceIsMissing()
    {
        const string aspireSource = "https://example.com/aspire/index.json";
        const string microsoftSource = "https://example.com/microsoft/index.json";

        string? addUsedSource = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? [new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "13.4.0", Source = string.Empty }]
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true));
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                        [
                            PackageChannel.CreateExplicitChannel(
                                PackageChannelNames.Stable,
                                PackageChannelQuality.Stable,
                                [
                                    new PackageMapping("Microsoft.*", microsoftSource),
                                    new PackageMapping("Aspire.*", aspireSource)
                                ],
                                cache)
                        ])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, _, _, nugetSource, _, _, _) =>
                {
                    addUsedSource = nugetSource;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add redis --apphost \"{appHostFile.FullName}\" --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(aspireSource, addUsedSource);
    }

    [Fact]
    public async Task AddCommandUnknownIntegrationNameInNonInteractiveModeDoesNotAddFirstPackage()
    {
        var testInteractionService = new TestInteractionService();
        var addPackageWasCalled = false;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
                    {
                        NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                            ? [
                                new NuGetPackage { Id = "AspireQuartz.Hosting", Version = "1.0.1", Source = "nuget" },
                                new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }
                            ]
                            : [];

                        return Task.FromResult<IEnumerable<NuGetPackage>>(packages.Where(package => filter?.Invoke(package.Id) ?? true));
                    }
                };

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    addPackageWasCalled = true;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add nonexistentpackage --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NonInteractiveRequiresExactPackageMatch, "nonexistentpackage"), testInteractionService.DisplayedErrors);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task AddCommandWithoutSourceUsesSelectedExplicitChannelSourceWithoutCreatingNuGetConfig()
    {
        const string packageId = "Aspire.Hosting.Redis";
        const string packageVersion = "1.0.1";
        const string nugetOrgSource = "https://api.nuget.org/v3/index.json";

        string? addUsedSource = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) => Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.PackagingServiceFactory = _ =>
            {
                var implicitCache = new FakeNuGetPackageCache();
                var nugetOrgCache = new FakeNuGetPackageCache
                {
                    GetPackagesAsyncCallback = (_, query, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                        query == HostingIntegrationMetadata.DiscoveryQuery
                            ? [new NuGetPackage { Id = packageId, Version = packageVersion, Source = nugetOrgSource }]
                            : [])
                };

                var explicitChannel = PackageChannel.CreateExplicitChannel(
                    PackageChannelNames.Stable,
                    PackageChannelQuality.Stable,
                    [new PackageMapping(PackageMapping.AllPackages, nugetOrgSource)],
                    nugetOrgCache);

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                        [PackageChannel.CreateImplicitChannel(implicitCache), explicitChannel])
                };
            };
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.AddPackageAsyncCallback = (_, _, _, nugetSource, _, _, _) =>
                {
                    addUsedSource = nugetSource;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse($"add {packageId} --apphost \"{appHostFile.FullName}\" --discovery-scope all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal(nugetOrgSource, addUsedSource);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.False(File.Exists(nugetConfigPath));
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
        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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
        var result = IntegrationPackageSearchService.GenerateFriendlyName((package, null!)); // Null is OK for this test.

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
        var channel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

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
    public async Task AddCommandPrompter_OrdersMicrosoftIntegrationsBeforeCommunityToolkit()
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
        var fakeCache = new FakeNuGetPackageCache();
        var channel = PackageChannel.CreateImplicitChannel(fakeCache);

        var packages = new[]
        {
            ("communitytoolkit-mongodb-extensions", new NuGetPackage { Id = "CommunityToolkit.Aspire.Hosting.MongoDB.Extensions", Version = "13.3.0", Source = "nuget" }, channel),
            ("mongodb", new NuGetPackage { Id = "Aspire.Hosting.MongoDB", Version = "13.4.0-pr.16882.gaf483c9e", Source = "pr-hive" }, channel),
        };

        // Act
        await prompter.PromptForIntegrationAsync(packages, CancellationToken.None).DefaultTimeout();

        // Assert
        Assert.NotNull(displayedPackages);
        Assert.Collection(
            displayedPackages!,
            package => Assert.Equal("Aspire.Hosting.MongoDB", package.Package.Id),
            package => Assert.Equal("CommunityToolkit.Aspire.Hosting.MongoDB.Extensions", package.Package.Id));
    }

    [Fact]
    public async Task AddCommandPrompter_SelectsHighestImplicitVersionWithoutPrompting()
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
        var channel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

        // Create multiple versions of the same package from same channel
        var packages = new[]
        {
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.2.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.1.0", Source = "nuget" }, channel),
            ("redis", new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.0.1-preview.1", Source = "nuget" }, channel),
        };

        // Act
        var result = await prompter.PromptForIntegrationVersionAsync(packages, configuredChannel: null, CancellationToken.None).DefaultTimeout();

        // Assert - should select the highest implicit version without prompting
        Assert.Null(displayedChoices);
        Assert.Equal("9.2.0", result.Package.Version);
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
        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

        var mappings = new[] { new PackageMapping("Aspire*", "https://preview-feed") };
        var explicitChannel = PackageChannel.CreateExplicitChannel("preview", PackageChannelQuality.Prerelease, mappings, fakeCache, new TestFeatures(), NullLogger.Instance);

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
        await prompter.PromptForIntegrationVersionAsync(packages, configuredChannel: null, CancellationToken.None).DefaultTimeout();

        // Assert - should show 2 choices: one version per channel
        Assert.NotNull(displayedChoices);
        Assert.Equal(2, displayedChoices!.Count);
    }

    [Fact]
    public async Task AddCommandPrompter_ShowsConfiguredChannelAsFirstChoiceWhenChannelPinned()
    {
        // Regression for https://github.com/microsoft/aspire/issues/18114.
        //
        // When the apphost pins a channel (e.g. a polyglot apphost that persists `"channel": "daily"`
        // in aspire.config.json), `aspire add` must surface that channel's package as the FIRST/default
        // menu option. Pre-fix the implicit/ambient channel was always rendered first, so the default
        // selection was the stable nuget.org version (e.g. 13.4.3) even though the project can only
        // restore from the pinned daily feed — producing a confusing default and a failed restore.
        List<string>? displayedLabels = null;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = (sp) =>
            {
                var mockInteraction = new TestInteractionService();
                mockInteraction.PromptForSelectionCallback = (message, choices, formatter, ct) =>
                {
                    var choicesList = choices.Cast<object>().ToList();
                    displayedLabels = choicesList.Select(formatter).ToList();
                    return choicesList.First();
                };
                return mockInteraction;
            };
        });
        using var provider = services.BuildServiceProvider();
        var interactionService = provider.GetRequiredService<IInteractionService>();

        var prompter = new AddCommandPrompter(interactionService);

        var fakeCache = new FakeNuGetPackageCache();
        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);
        var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], fakeCache, new TestFeatures(), NullLogger.Instance);

        // The implicit (ambient) channel surfaces a higher-precedence STABLE version; the pinned daily
        // channel surfaces a PRERELEASE version. Pre-fix the higher stable version was always the default.
        var packages = new[]
        {
            ("storage", new NuGetPackage { Id = "Aspire.Hosting.Azure.Storage", Version = "13.4.3", Source = "nuget" }, implicitChannel),
            ("storage", new NuGetPackage { Id = "Aspire.Hosting.Azure.Storage", Version = "13.5.0-preview.1", Source = "daily" }, dailyChannel),
        };

        var result = await prompter.PromptForIntegrationVersionAsync(packages, configuredChannel: "daily", CancellationToken.None).DefaultTimeout();

        Assert.NotNull(displayedLabels);
        // The pinned (daily) channel is the first/default choice; the implicit channel follows.
        Assert.Equal("daily", displayedLabels![0]);
        // Selecting the default (first) choice resolves to the daily channel's prerelease package.
        Assert.Equal("13.5.0-preview.1", result.Package.Version);
        Assert.Same(dailyChannel, result.Channel);
    }

    [Fact]
    public async Task AddCommandNonInteractiveTypeScriptAppHostPinnedToDailyPrefersDailyChannelOverImplicitStable()
    {
        // Regression for https://github.com/microsoft/aspire/issues/18114.
        //
        // Repro: a polyglot (TypeScript) apphost created by a daily CLI persists `"channel": "daily"`
        // in aspire.config.json, and its NuGet.config maps Aspire* to the daily (dotnet9) feed only.
        // `aspire add azure-storage --non-interactive` discovers BOTH the implicit channel (ambient
        // nuget.org -> stable 13.4.3) and the pinned daily channel (dotnet9 -> 13.5.0-preview.1).
        //
        // Pre-fix: GetPackageByInteractiveFlow ranked the implicit channel first, so the non-interactive
        // path auto-selected the stable 13.4.3 — which the project then could NOT restore from the daily
        // feed (the dotnet9 feed has no stable 13.4.3), surfacing as a hard restore failure.
        //
        // Post-fix: a pinned channel outranks the implicit channel, so the daily 13.5.0-preview.1 package
        // is selected and restore from the pinned feed succeeds.
        var addedPackageId = string.Empty;
        var addedPackageVersion = string.Empty;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        File.WriteAllText(appHostFile.FullName, string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
            {
              "channel": "daily"
            }
            """);

        var implicitCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? [CreatePackage("Aspire.Hosting.Azure.Storage", "13.4.3")]
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };
        var dailyCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, _, _, _) =>
            {
                NuGetPackage[] packages = query == HostingIntegrationMetadata.DiscoveryQuery
                    ? [CreatePackage("Aspire.Hosting.Azure.Storage", "13.5.0-preview.1")]
                    : [];

                return Task.FromResult<IEnumerable<NuGetPackage>>(filter is null ? packages : packages.Where(package => filter(package.Id)).ToArray());
            }
        };

        var tsFactory = new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true));
        tsFactory.Project.AddPackageAsyncCallback = (context, _) =>
        {
            addedPackageId = context.PackageId;
            addedPackageVersion = context.PackageVersion;
            return Task.FromResult(true);
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance),
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], dailyCache, new TestFeatures(), NullLogger.Instance)
                ])
            };
        });
        services.AddSingleton<IAppHostProjectFactory>(tsFactory);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        // Use the fully-qualified package id so it is an exact match (no fuzzy fallback) in non-interactive mode.
        var result = command.Parse($"add Aspire.Hosting.Azure.Storage --apphost \"{appHostFile.FullName}\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal("Aspire.Hosting.Azure.Storage", addedPackageId);
        Assert.Equal("13.5.0-preview.1", addedPackageVersion);
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
        // PR-hive packages are discovered through the package-search code path: the
        // explicit channel maps to a separate NuGet source that, when queried, returns
        // a package pinned to the current CLI version.
        var cliVersion = VersionHelper.GetDefaultSdkVersion();

        var (exitCode, selectedVersion, prompted) = await RunAddRedisWithHiveScenarioAsync(
            configureHives: workspace =>
            {
                var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
                hivesDir.Create();
                hivesDir.CreateSubdirectory("pr-12345");
            },
            searchCallback: nugetSource => nugetSource is null
                ? new[] { new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.2" } }
                : new[] { new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "pr-hive", Version = cliVersion } },
            promptFailureMessage: "Should not prompt when the current CLI version is available in a PR hive.");

        Assert.Equal(0, exitCode);
        Assert.False(prompted);
        Assert.Equal(cliVersion, selectedVersion);
    }

    [Fact]
    public async Task AddCommand_WithPrHiveInInteractiveMode_PrefersCurrentCliVersion()
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();

        var (exitCode, selectedVersion, prompted) = await RunAddRedisWithHiveScenarioAsync(
            configureHives: workspace =>
            {
                var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
                hivesDir.Create();
                hivesDir.CreateSubdirectory("pr-12345");
            },
            searchCallback: nugetSource => nugetSource is null
                ? [new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.2" }]
                : [new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "pr-hive", Version = cliVersion }],
            promptFailureMessage: "Should not prompt when the current CLI version is available in a PR hive, even in interactive mode.",
            interactive: true);

        Assert.Equal(0, exitCode);
        Assert.False(prompted);
        Assert.Equal(cliVersion, selectedVersion);
    }

    [Fact]
    public async Task AddCommand_WithLocalHive_PrefersCurrentCliVersion()
    {
        // The local channel enumerates .nupkg files directly from disk and does not call
        // package search — only the implicit channel goes through SearchPackagesAsync,
        // which here returns a stale version that must lose to the on-disk CLI-version match.
        var cliVersion = VersionHelper.GetDefaultSdkVersion();

        var (exitCode, selectedVersion, prompted) = await RunAddRedisWithHiveScenarioAsync(
            configureHives: workspace =>
            {
                var localPackagesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives", PackageChannelNames.Local, "packages"));
                localPackagesDir.Create();
                // Aspire.Hosting drives GetLocalHivePinnedVersion; Aspire.Hosting.Redis is the integration we add.
                File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.{cliVersion}.nupkg"), string.Empty);
                File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.Redis.{cliVersion}.nupkg"), string.Empty);
            },
            searchCallback: _ => new[] { new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.2" } },
            promptFailureMessage: "Should not prompt when the current CLI version is available in the local hive.");

        Assert.Equal(0, exitCode);
        Assert.False(prompted);
        Assert.Equal(cliVersion, selectedVersion);
    }

    [Fact]
    public async Task AddCommand_WithLocalAndPrHives_PrefersHiveMatchingCurrentCliVersion()
    {
        // F (cross-channel mixing precedence): both `local` and `pr-12345` hives are populated.
        // The local hive is pinned to the current CLI version; pr-12345 is pinned to a stale version.
        // AddCommand routes through VersionHelper.TryGetCurrentCliVersionMatch, which iterates
        // candidates from local-build channels (`IsLocalBuildChannel` = local | pr-* | run-*) and
        // returns the first version that exactly matches GetDefaultSdkVersion(). Only the local
        // hive's package matches, so it wins regardless of which channel ran first.
        //
        // NOTE on undocumented contract: when BOTH hives contain a CLI-version-exact match,
        // selection falls through to enumeration order of GetChannelsAsync's
        // HivesDirectory.GetDirectories() (filesystem-dependent, typically alphabetical),
        // combined with Parallel.ForEachAsync ordering in IntegrationPackageSearchService.
        // No deterministic precedence is currently defined for that case. Flagged for policy.
        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        const string staleVersion = "13.0.0-pr.99999.gstale01";

        var (exitCode, selectedVersion, prompted) = await RunAddRedisWithHiveScenarioAsync(
            configureHives: workspace =>
            {
                var hivesRoot = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
                var localPackagesDir = new DirectoryInfo(Path.Combine(hivesRoot.FullName, PackageChannelNames.Local, "packages"));
                var prPackagesDir = new DirectoryInfo(Path.Combine(hivesRoot.FullName, "pr-12345", "packages"));
                localPackagesDir.Create();
                prPackagesDir.Create();

                File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.{cliVersion}.nupkg"), string.Empty);
                File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.Redis.{cliVersion}.nupkg"), string.Empty);

                File.WriteAllText(Path.Combine(prPackagesDir.FullName, $"Aspire.Hosting.{staleVersion}.nupkg"), string.Empty);
                File.WriteAllText(Path.Combine(prPackagesDir.FullName, $"Aspire.Hosting.Redis.{staleVersion}.nupkg"), string.Empty);
            },
            searchCallback: _ => new[] { new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.2" } },
            promptFailureMessage: "Should not prompt; CLI-version match in local hive should win.");

        Assert.Equal(0, exitCode);
        Assert.False(prompted);
        Assert.Equal(cliVersion, selectedVersion);
        Assert.NotEqual(staleVersion, selectedVersion);
    }

    [Fact]
    public async Task AddCommand_WithIdentityPackagesOverrideEmulatingStable_PrefersCurrentCliVersion()
    {
        // Emulating a released build via ASPIRE_CLI_PACKAGES / the sidecar `packages` field: the
        // synthesized channel is NAMED after the emulated identity ("stable", a non-local-build
        // name) yet resolves Aspire.* from a local directory. `aspire add` must still treat it as a
        // CLI-version-pinned local source so the exact-CLI-version package wins over the implicit
        // channel's stale version — i.e. the IsBackedByLocalPackageDirectory recognition, not the
        // channel name, drives selection. Regression guard for the identity-sidecar emulation bug
        // where a stable/daily/staging emulated name excluded the local channel from add resolution.
        var cliVersion = VersionHelper.GetDefaultSdkVersion();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var identityPackagesDir = workspace.CreateDirectory("identity-packages");
        // Aspire.Hosting drives GetLocalHivePinnedVersion; Aspire.Hosting.Redis is the integration we add.
        File.WriteAllText(Path.Combine(identityPackagesDir.FullName, $"Aspire.Hosting.{cliVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(identityPackagesDir.FullName, $"Aspire.Hosting.Redis.{cliVersion}.nupkg"), string.Empty);

        var selectedPackageVersion = string.Empty;
        var promptedForVersion = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                identityChannel: PackageChannelNames.Stable,
                identityVersion: cliVersion,
                identityOverridden: true,
                identityPackagesDirectory: identityPackagesDir);

            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException("Should not prompt when the current CLI version is available in the local package override.");
                };
                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                // Only the implicit channel goes through package search; it returns a stale version
                // that must lose to the on-disk CLI-version match from the emulated-stable local source.
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                    (0, new[] { new NuGetPackage { Id = "Aspire.Hosting.Redis", Source = "implicit", Version = "13.2.2" } });

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

    /// <summary>
    /// Shared scaffolding for "aspire add redis" + hive precedence tests. The three tests
    /// (PR-hive / local-hive / both-hives) differ only in (a) how the hive directory is
    /// laid out on disk and (b) what the package-search mock returns. Everything else
    /// — workspace, prompter that fails on prompt, project locator, AddPackage capture —
    /// is identical.
    /// </summary>
    private async Task<(int ExitCode, string SelectedVersion, bool PromptInvoked)> RunAddRedisWithHiveScenarioAsync(
        Action<TemporaryWorkspace> configureHives,
        Func<FileInfo?, NuGetPackage[]> searchCallback,
        string promptFailureMessage,
        bool interactive = false)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        configureHives(workspace);

        var selectedPackageVersion = string.Empty;
        var promptedForVersion = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => interactive
                ? TestHelpers.CreateInteractiveHostEnvironment()
                : TestHelpers.CreateNonInteractiveHostEnvironment();
            options.AddCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestAddCommandPrompter(interactionService);
                prompter.PromptForIntegrationVersionCallback = (packages) =>
                {
                    promptedForVersion = true;
                    throw new InvalidOperationException(promptFailureMessage);
                };
                return prompter;
            };

            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, invocationOptions, cancellationToken) =>
                {
                    return (0, searchCallback(nugetSource));
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

        return (exitCode, selectedPackageVersion, promptedForVersion);
    }

    private static NuGetPackage CreatePackage(string id, string version)
    {
        return new NuGetPackage
        {
            Id = id,
            Source = "nuget",
            Version = version
        };
    }

    private static (string? Name, string? Package, string? Version)[] ReadIntegrationResults(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(element => (
                Name: element.GetProperty("name").GetString(),
                Package: element.GetProperty("package").GetString(),
                Version: element.GetProperty("version").GetString()))
            .ToArray();
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace, string identityChannel)
    {
        var aspireDirectory = workspace.CreateDirectory(".aspire");
        var hivesDirectory = new DirectoryInfo(Path.Combine(aspireDirectory.FullName, "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(aspireDirectory.FullName, "cache"));
        var sdksDirectory = new DirectoryInfo(Path.Combine(aspireDirectory.FullName, "sdks"));
        var logsDirectory = new DirectoryInfo(Path.Combine(aspireDirectory.FullName, "logs"));

        return new CliExecutionContext(
            workspace.WorkspaceRoot,
            hivesDirectory,
            cacheDirectory,
            sdksDirectory,
            logsDirectory,
            Path.Combine(logsDirectory.FullName, "test.log"),
            identityChannel: identityChannel);
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

    public override Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string? configuredChannel, CancellationToken cancellationToken)
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

            // Fuzzy fallback only fires in interactive mode after the Layer-3 fix for #17724.
            // The default test host environment is non-interactive (mirroring CI), so opt this
            // fixture into the interactive path explicitly: the test asserts that an interactive
            // user can still discover PostgreSQL by typing "postgre".
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

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
    public async Task AddCommand_NonInteractive_NoExactMatchWithoutVersion_FailsInsteadOfFuzzyAutoPick_Regression17724()
    {
        // Regression for https://github.com/microsoft/aspire/issues/17724.
        //
        // Pre-fix: `aspire add kube --non-interactive` had no exact match for "kube" (none of the
        //   packages are literally named "kube"), so AddCommand fell back to fuzzy search. The fuzzy
        //   candidate list was then passed to GetPackageByInteractiveFlow, which in non-interactive
        //   mode auto-selected `distinctPackages.First()` (AddCommand.cs:368-369) and silently added
        //   the wrong package. In the user's report this was Aspire.Hosting.Azure because the
        //   companion Layer-1 bug (#17725 / IntegrationPackageSearchService narrowing) had filtered
        //   prerelease packages out, leaving Azure as the only fuzzy candidate.
        //
        // Fix: AddCommand now refuses to fall back to fuzzy search whenever the host is non-interactive
        //   and no exact match was found, regardless of whether --version was supplied. The error
        //   surfaces the new NonInteractiveRequiresExactPackageMatch resource so the user/script
        //   knows to supply the full package id or friendly name.
        //
        // This test uses the simpler C# project flow (TestDotNetCliRunner stub) because the bug is
        // in AddCommand's non-interactive handling, not in package discovery — the discovery path is
        // covered by the cross-language parity test above. The Aspire.Hosting.Azure and
        // Aspire.Hosting.Kubernetes packages both fuzzy-match "kube"; pre-fix the first one
        // (Aspire.Hosting.Azure, alphabetical) would have been silently picked.
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
                    return (
                        0,
                        new NuGetPackage[]
                        {
                            new() { Id = "Aspire.Hosting.Azure", Source = "nuget", Version = "9.2.0" },
                            new() { Id = "Aspire.Hosting.Kubernetes", Source = "nuget", Version = "9.2.0" }
                        });
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
        var result = command.Parse("add kube");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
        Assert.False(addPackageWasCalled, "AddPackageAsync must not be called when there is no exact match in non-interactive mode.");
        Assert.Contains(string.Format(AddCommandStrings.NonInteractiveRequiresExactPackageMatch, "kube"), testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task AddCommand_NonInteractive_ExactMatchWithoutVersion_StillSucceeds()
    {
        // Companion regression guard for #17724: ensures the new non-interactive guard ONLY fires
        // when there is no exact match. An exact match by package id (or friendly name) must still
        // install successfully — this is the documented happy path for CI/scripted usage.
        var addedPackage = string.Empty;
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
                    return (
                        0,
                        new NuGetPackage[]
                        {
                            new() { Id = "Aspire.Hosting.Azure", Source = "nuget", Version = "9.2.0" },
                            new() { Id = "Aspire.Hosting.Kubernetes", Source = "nuget", Version = "9.2.0" }
                        });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackage = packageName;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        // "kubernetes" is the friendly name (Aspire.Hosting.Kubernetes → friendlyName "kubernetes"),
        // so this is an exact match and must succeed.
        var result = command.Parse("add kubernetes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal("Aspire.Hosting.Kubernetes", addedPackage);
    }

    [Fact]
    public async Task AddCommand_Interactive_SingleFuzzyMatchPromptsBeforeAdding_Regression17724()
    {
        var promptedPackages = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();
        var addedPackage = string.Empty;

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
                    return packages.Single();
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    return (
                        0,
                        new NuGetPackage[]
                        {
                            new() { Id = "Aspire.Hosting.Azure", Source = "nuget", Version = "9.2.0" }
                        });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackage = packageName;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add kube");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var promptedPackage = Assert.Single(promptedPackages);
        Assert.Equal(0, exitCode);
        Assert.Equal("Aspire.Hosting.Azure", promptedPackage.Package.Id);
        Assert.Equal("Aspire.Hosting.Azure", addedPackage);
    }

    [Fact]
    public async Task AddCommand_Interactive_NoFuzzyMatchSinglePackagePromptsBeforeAdding()
    {
        var promptedPackages = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();
        var displayedSubtleMessage = string.Empty;
        var addedPackage = string.Empty;
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
                    promptedPackages.AddRange(packages);
                    return packages.Single();
                };

                return prompter;
            };
            options.ProjectLocatorFactory = _ => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (dir, query, exactMatch, prerelease, take, skip, nugetSource, useCache, options, cancellationToken) =>
                {
                    return (
                        0,
                        new NuGetPackage[]
                        {
                            new() { Id = "Aspire.Hosting.Redis", Source = "nuget", Version = "9.2.0" }
                        });
                };

                runner.AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                {
                    addedPackage = packageName;
                    return 0;
                };

                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AddCommand>();
        var result = command.Parse("add zzzzzzzzzz");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var promptedPackage = Assert.Single(promptedPackages);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Format(AddCommandStrings.NoPackagesMatchedSearchTerm, "zzzzzzzzzz"), displayedSubtleMessage);
        Assert.Equal("Aspire.Hosting.Redis", promptedPackage.Package.Id);
        Assert.Equal("Aspire.Hosting.Redis", addedPackage);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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

        Assert.Equal(CliExitCodes.FailedToAddPackage, exitCode);
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

            // Fuzzy fallback only fires in interactive mode after the Layer-3 fix for #17724;
            // see companion comment on AddCommand_WithStartsWith_FindsMatchUsingFuzzySearch.
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();

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
