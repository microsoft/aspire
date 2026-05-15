// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.InternalTesting;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Locks in route-aware behavior of the CLI's "version X is available"
/// notification. Each install route must surface the command that actually
/// updates the binary the user is running — running the script-route
/// command from a Homebrew / WinGet / dotnet-tool / PR install is the bug
/// pattern this test class guards against.
/// </summary>
public class UpdateNotificationRouteTests(ITestOutputHelper outputHelper)
{
    // Per-source expected notification command. We do not enumerate the
    // dotnet-tool / Pr cases here because their commands depend on the
    // running binary's path / identity channel; those are exercised
    // separately in UpgradeInstructionProviderTests. The rows here lock in
    // that the notifier wires its output through the same provider, not the
    // legacy hardcoded "aspire update" / dotnet-tool special case.
    [Theory]
    [InlineData("winget", "winget upgrade Microsoft.Aspire")]
    [InlineData("brew", "brew upgrade --cask aspire")]
    [InlineData("localhive", "Run ./localhive.sh (Linux/macOS) or .\\localhive.ps1 (Windows) in the local hive directory.")]
    [InlineData("script", "aspire update --self")]
    [InlineData(null, "Aspire couldn't determine how this CLI was installed")]
    // Unrecognized sidecar source value (a route added by a future build
    // that this CLI doesn't know about yet). The reader returns
    // InstallSource.Unknown with RawSource preserved; the notifier must
    // still surface an actionable hint rather than printing the raw
    // unrecognized name.
    [InlineData("future-route-name", "Aspire couldn't determine how this CLI was installed")]
    public async Task NotifyIfUpdateAvailable_RouteAwareCommand_MatchesUpgradeInstructionProvider(string? sidecarSource, string expectedCommand)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var selfInfo = new InstallationInfo
        {
            Path = "/test/aspire",
            CanonicalPath = "/test/aspire",
            Route = sidecarSource,
            Status = sidecarSource is null
                ? InstallationInfoStatus.NotProbed
                : InstallationInfoStatus.Ok,
        };

        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetCliPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>(
                [
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
                // Pin the current version so the fake "9.5.0" available
                // package always reads as newer regardless of the test
                // runner's actual version.
                return new CliUpdateNotifierWithPackageVersionOverride(
                    "9.4.0", logger, nuGetPackageCache, service,
                    sp.GetRequiredService<IInstallationDiscovery>(),
                    sp.GetRequiredService<IUpgradeInstructionProvider>(),
                    sp.GetRequiredService<CliExecutionContext>(),
                    sp.GetRequiredService<WingetFirstRunProbe>());
            };
        });

        // Replace the real InstallationDiscovery (which reads
        // Environment.ProcessPath, not test overrides) with a fake that
        // surfaces the route under test. Done after CreateServiceCollection
        // so the last registration wins.
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(selfInfo));

        using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ICliUpdateNotifier>();

        await notifier.CheckForCliUpdatesAsync(workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();
        notifier.NotifyIfUpdateAvailable();

        Assert.NotNull(interactionService);
        Assert.Contains(expectedCommand, interactionService.LastVersionUpdateCommand, StringComparison.Ordinal);
    }
}
