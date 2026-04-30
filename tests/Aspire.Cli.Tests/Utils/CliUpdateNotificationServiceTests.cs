// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Utils;

public class CliUpdateNotificationServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PrereleaseWillRecommendUpgradeToPrereleaseOnSameVersionFamily()
    {
        var currentVersion = VersionHelper.GetDefaultTemplateVersion();
        TaskCompletionSource<string> suggestedVersionTcs = new();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = (sp) =>
            {
                var cache = new FakeNuGetPackageCache { GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    // Should be ignored because it's lower than current prerelease version.
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.3.1", Source = "nuget.org" },

                    // Should be selected because it is higher than 9.4.0-dev (dev and preview sort using alphabetical sort).
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.4.0-preview", Source = "nuget.org" }, 

                    // Should be ignored because it is lower than 9.4.0-dev (dev and preview sort using alpha).
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.4.0-beta", Source = "nuget.org" }
                ]) }; return cache;
            };

            configure.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.DisplayVersionUpdateNotificationCallback = (newerVersion) =>
                {
                    suggestedVersionTcs.SetResult(newerVersion);
                };

                return interactionService;
            };

            configure.CliUpdateNotifierFactory = (sp) =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var interactionService = sp.GetRequiredService<IInteractionService>();

                // Use a custom notifier that overrides the current version
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0-dev", logger, nuGetPackageCache, interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();
        var suggestedVersion = await suggestedVersionTcs.Task.DefaultTimeout();

        Assert.Equal("9.4.0-preview", suggestedVersion);
    }

    [Fact]
    public async Task PrereleaseWillRecommendUpgradeToStableInCurrentVersionFamily()
    {
        var currentVersion = VersionHelper.GetDefaultTemplateVersion();
        TaskCompletionSource<string> suggestedVersionTcs = new();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = (sp) =>
            {
                var cache = new FakeNuGetPackageCache { GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    // Should be selected because stable sorts higher than preview.
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.4.0", Source = "nuget.org" },

                    // Should be ignored because its prerelease but in a higher version family.
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0-preview", Source = "nuget.org" },
                ]) }; return cache;
            };

            configure.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.DisplayVersionUpdateNotificationCallback = (newerVersion) =>
                {
                    suggestedVersionTcs.SetResult(newerVersion);
                };

                return interactionService;
            };

            configure.CliUpdateNotifierFactory = (sp) =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var interactionService = sp.GetRequiredService<IInteractionService>();

                // Use a custom notifier that overrides the current version
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0-dev", logger, nuGetPackageCache, interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();
        var suggestedVersion = await suggestedVersionTcs.Task.DefaultTimeout();

        Assert.Equal("9.4.0", suggestedVersion);
    }

    [Fact]
    public async Task StableWillOnlyRecommendGoingToNewerStable()
    {
        var currentVersion = VersionHelper.GetDefaultTemplateVersion();
        TaskCompletionSource<string> suggestedVersionTcs = new();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = (sp) =>
            {
                var cache = new FakeNuGetPackageCache { GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    // Should be ignored because its stable in a higher version family.
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0", Source = "nuget.org" }, 

                    // Should be ignored because its prerelease but in a (even) higher version family.
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.6.0-preview", Source = "nuget.org" },
                ]) }; return cache;
            };

            configure.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.DisplayVersionUpdateNotificationCallback = (newerVersion) =>
                {
                    suggestedVersionTcs.SetResult(newerVersion);
                };

                return interactionService;
            };

            configure.CliUpdateNotifierFactory = (sp) =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var interactionService = sp.GetRequiredService<IInteractionService>();

                // Use a custom notifier that overrides the current version
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0", logger, nuGetPackageCache, interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();
        var suggestedVersion = await suggestedVersionTcs.Task.DefaultTimeout();

        Assert.Equal("9.5.0", suggestedVersion);
    }

    [Fact]
    public async Task NotifyIfUpdateAvailable_UsesDotnetToolCommandForNativeAotToolStorePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting("/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/any/linux-x64/aspire");
        TestInteractionService? interactionService = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0", Source = "nuget.org" }
                ])
            };

            configure.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };

            configure.CliUpdateNotifierFactory = sp =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var service = sp.GetRequiredService<IInteractionService>();
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0", logger, nuGetPackageCache, service);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();

        Assert.NotNull(interactionService);
        Assert.Equal("dotnet tool update -g Aspire.Cli", interactionService.LastVersionUpdateCommand);
    }

    [Fact]
    public async Task NotifyIfUpdateAvailable_UsesToolPathCommandForCustomToolPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var tempDirectory = new TestTempDirectory();
        var toolPath = Path.Combine(tempDirectory.Path, "custom tool path");
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(CreateCustomToolPathInstall(toolPath));
        TestInteractionService? interactionService = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0", Source = "nuget.org" }
                ])
            };

            configure.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };

            configure.CliUpdateNotifierFactory = sp =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var service = sp.GetRequiredService<IInteractionService>();
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0", logger, nuGetPackageCache, service);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();

        Assert.NotNull(interactionService);
        Assert.Equal($"dotnet tool update --tool-path \"{toolPath}\" Aspire.Cli", interactionService.LastVersionUpdateCommand);
    }

    [Fact]
    public async Task NotifyIfUpdateAvailable_UsesAspireUpdateCommandForStandaloneArchivePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting("/home/test/.aspire/bin/aspire");
        TestInteractionService? interactionService = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0", Source = "nuget.org" }
                ])
            };

            configure.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };

            configure.CliUpdateNotifierFactory = sp =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var service = sp.GetRequiredService<IInteractionService>();
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0", logger, nuGetPackageCache, service);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();

        Assert.NotNull(interactionService);
        Assert.Equal("aspire update", interactionService.LastVersionUpdateCommand);
    }

    [Fact]
    public async Task StableWillNotRecommendUpdatingToPreview()
    {
        var currentVersion = VersionHelper.GetDefaultTemplateVersion();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = (sp) =>
            {
                var cache = new FakeNuGetPackageCache { GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.4.0-preview", Source = "nuget.org" },
                    new NuGetPackage { Id = "Aspire.Cli", Version = "9.5.0-preview", Source = "nuget.org" },
                ]) }; return cache;
            };

            configure.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.DisplayVersionUpdateNotificationCallback = (newerVersion) =>
                {
                    Assert.Fail("Should not suggest a preview version when current version is stable.");
                };

                return interactionService;
            };

            configure.CliUpdateNotifierFactory = (sp) =>
            {
                var logger = sp.GetRequiredService<ILogger<CliUpdateNotifier>>();
                var nuGetPackageCache = sp.GetRequiredService<INuGetPackageCache>();
                var interactionService = sp.GetRequiredService<IInteractionService>();

                // Use a custom notifier that overrides the current version
                return new CliUpdateNotifierWithPackageVersionOverride("9.4.0", logger, nuGetPackageCache, interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();
    }

    [Fact]
    public async Task NotifyIfUpdateAvailableAsync_WithNewerStableVersion_DoesNotThrow()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);

        // Replace the NuGetPackageCache with our test implementation
        var nugetCache = new FakeNuGetPackageCache
        {
            GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([
                new NuGetPackage { Id = "Aspire.Cli", Version = "9.0.0", Source = "nuget.org" }
            ])
        };
        services.AddSingleton<INuGetPackageCache>(nugetCache);
        services.AddSingleton<ICliUpdateNotifier, CliUpdateNotifier>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICliUpdateNotifier>();

        // Act & Assert (should not throw)
        await service.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        service.NotifyIfUpdateAvailable();
    }

    [Fact]
    public async Task NotifyIfUpdateAvailableAsync_WithEmptyPackages_DoesNotThrow()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);

        // Replace the NuGetPackageCache with our test implementation
        services.AddSingleton<INuGetPackageCache>(new FakeNuGetPackageCache());
        services.AddSingleton<ICliUpdateNotifier, CliUpdateNotifier>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICliUpdateNotifier>();

        // Act & Assert (should not throw)
        await service.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        service.NotifyIfUpdateAvailable();
    }

    private static string CreateCustomToolPathInstall(string toolPath)
    {
        var processPath = Path.Combine(toolPath, GetAspireExecutableName());
        var storeExecutablePath = Path.Combine(
            toolPath,
            ".store",
            "aspire.cli",
            "9.4.0",
            "aspire.cli.linux-x64",
            "9.4.0",
            "tools",
            "net10.0",
            "linux-x64",
            GetAspireExecutableName());

        Directory.CreateDirectory(toolPath);
        Directory.CreateDirectory(Path.GetDirectoryName(storeExecutablePath)!);
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(storeExecutablePath, string.Empty);

        return processPath;
    }

    private static string GetAspireExecutableName()
    {
        return OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
    }
}

internal sealed class CliUpdateNotifierWithPackageVersionOverride(string currentVersion, ILogger<CliUpdateNotifier> logger, INuGetPackageCache nuGetPackageCache, IInteractionService interactionService) : CliUpdateNotifier(logger, nuGetPackageCache, interactionService)
{
    protected override SemVersion? GetCurrentVersion()
    {
        return SemVersion.Parse(currentVersion, SemVersionStyles.Strict);
    }
}
