// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")]);
            }
        };
        var dailyCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref dailyHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], dailyCache, new TestFeatures())
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")]);
            }
        };
        var stagingCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref stagingHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Stable);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, PackageChannelQuality.Both, [new PackageMapping("Aspire*", "staging")], stagingCache, new TestFeatures())
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
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>(
                    prerelease
                        ? [CreatePackage("Aspire.Hosting.Redis", "1.0.0"), CreatePackage("Aspire.Hosting.Foundry", "1.0.0-preview.1")]
                        : [CreatePackage("Aspire.Hosting.Redis", "1.0.0")]);
            }
        };
        // Stable channel: Quality.Stable. PackageChannel only issues prerelease=false queries against it,
        // so Foundry (prerelease-only) never appears regardless of what the cache could return.
        var stableHits = 0;
        var stableCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref stableHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Stable);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, [new PackageMapping("Aspire*", "stable")], stableCache, new TestFeatures())
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref implicitHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")]);
            }
        };
        var dailyCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                Interlocked.Increment(ref dailyHits);
                return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "daily")], dailyCache, new TestFeatures())
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")])
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")])
        };
        var stagingCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")])
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace, PackageChannelNames.Staging);
            options.InteractionServiceFactory = _ => testInteractionService;
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, PackageChannelQuality.Both, [new PackageMapping("Aspire*", "staging")], stagingCache, new TestFeatures())
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
    public async Task IntegrationSearchCommandStagingStampedCliWithPinnedStagingApphostQueriesBothImplicitAndStagingChannelsAndSurfacesPrereleaseOnlyPackages()
    {
        // High-confidence shipping-shape regression guard for #17724 and #17725.
        //
        // This test simulates EXACTLY what a real CLI built and shipped as staging will do when
        // the user runs `aspire add <name>` against a polyglot apphost that `aspire new` created:
        //
        //   * The CLI binary is stamped `AspireCliChannel=staging` -> `IdentityChannel == "staging"`.
        //     This triggers the real PackagingService.GetChannelsAsync to synthesize a real staging
        //     channel alongside implicit + stable (no fake TestPackagingService is used here).
        //   * `aspire new` writes `"channel": "staging"` into aspire.config.json (see
        //     CliTemplateFactory.TypeScriptStarterTemplate). We mirror that here.
        //   * There are NO PR hives. This is a real shipped install, not a dogfood/PR build.
        //
        // Pre-fix (the regression introduced before 13.4): the gate narrowed the search to ONLY the
        // pinned staging channel. Implicit was excluded. Prerelease-only integrations (e.g.,
        // Aspire.Hosting.Foundry) were invisible because the only feed queried was the staging
        // feed, which doesn't surface them. The `aspire add kubernetes` regression had the same
        // root cause: kubernetes was reachable via implicit (nuget.org) but invisible under the
        // narrowed staging-only search.
        //
        // Post-fix invariants verified here:
        //   (i)  BOTH implicit AND the synthesized staging channel are queried (cache call count
        //        is >= 2). Pre-fix this would have been exactly 1.
        //   (ii) A prerelease-only package returned by the cache only when prerelease=true (which
        //        is what Quality.Both channels request) is reachable to the user.
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
            GetIntegrationPackagesAsyncCallback = (_, prerelease, _, _) =>
            {
                Interlocked.Increment(ref totalCacheCalls);
                if (prerelease)
                {
                    Interlocked.Increment(ref prereleaseRequested);
                    return Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Foundry", "13.4.0-rc.1")]);
                }
                return Task.FromResult<IEnumerable<NuGetPackage>>([]);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Stamp the running CLI as the staging release identity. The real PackagingService
            // (left un-overridden here) reads this from CliExecutionContext.IdentityChannel and
            // synthesizes the staging channel automatically (see PackagingService.GetChannelsAsync
            // -> stagingIdentityChannel branch).
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

        // (ii) The prerelease-only package is reachable to the user.
        var integration = Assert.Single(ReadIntegrationResults(rawJson));
        Assert.Equal("Aspire.Hosting.Foundry", integration.Package);
        Assert.Equal("13.4.0-rc.1", integration.Version);

        // (i) Both implicit AND staging were queried. Pre-fix narrowing would have produced exactly 1 call.
        // Real PackagingService.GetChannelsAsync under IdentityChannel=Staging returns at least
        // [implicit, stable, staging]; the IPSS gate now lets all of them through (hasHives=false,
        // configuredChannel="staging" -> not empty -> gate evaluates true). At minimum the implicit
        // and staging channels must have run, so we require >= 2 calls. Using `>= 2` rather than
        // `== N` keeps the test robust to PackagingService adding additional explicit channels
        // (e.g., stable) without weakening the regression guard.
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
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "1.0.0")])
        };
        var explicitCache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([CreatePackage("Aspire.Hosting.Redis", "2.0.0")])
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([
                    PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures()),
                    PackageChannel.CreateExplicitChannel("test-hive", PackageChannelQuality.Both, [new PackageMapping("Aspire*", "test-hive")], explicitCache, new TestFeatures())
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
        var channel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures());

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
        var channel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures());

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
        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures());
        
        var mappings = new[] { new PackageMapping("Aspire*", "https://preview-feed") };
        var explicitChannel = PackageChannel.CreateExplicitChannel("preview", PackageChannelQuality.Prerelease, mappings, fakeCache, new TestFeatures());

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
        string promptFailureMessage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        configureHives(workspace);

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
