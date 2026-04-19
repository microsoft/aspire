// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Factory for creating AppHostServerProject instances with required dependencies.
/// </summary>
internal interface IAppHostServerProjectFactory
{
    Task<IAppHostServerProject> CreateAsync(string appPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory implementation that creates IAppHostServerProject instances.
/// Chooses between DotNetBasedAppHostServerProject (dev mode) and PrebuiltAppHostServer (bundle mode).
/// </summary>
internal sealed class AppHostServerProjectFactory(
    IDotNetCliRunner dotNetCliRunner,
    IPackagingService packagingService,
    IConfigurationService configurationService,
    IBundleService bundleService,
    BundleNuGetService bundleNuGetService,
    IDotNetSdkInstaller sdkInstaller,
    ILoggerFactory loggerFactory,
    ILogger<AppHostServerProjectFactory> logger) : IAppHostServerProjectFactory
{
    public async Task<IAppHostServerProject> CreateAsync(string appPath, CancellationToken cancellationToken = default)
    {
        var socketPath = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");

        logger.LogDebug("Creating AppHost server project for {AppPath}. IsBundle={IsBundle}. {BundleState}",
            appPath,
            bundleService.IsBundle,
            bundleService.GetLayoutState().Describe());

        // Priority 1: Check for dev mode (ASPIRE_REPO_ROOT or running from Aspire source repo)
        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot(appPath);
        if (repoRoot is not null)
        {
            logger.LogDebug("Using repository AppHost server project from {RepoRoot}", repoRoot);
            return new DotNetBasedAppHostServerProject(
                appPath,
                socketPath,
                repoRoot,
                dotNetCliRunner,
                packagingService,
                configurationService,
                loggerFactory.CreateLogger<DotNetBasedAppHostServerProject>());
        }

        logger.LogDebug("Repository AppHost server project unavailable for {AppPath}. ASPIRE_REPO_ROOT set={HasRepoRootEnvVar}.",
            appPath,
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BundleDiscovery.RepoRootEnvVar)));

        // Priority 2: Ensure bundle is extracted and check for layout
        var layout = await bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);

        if (!HasUsableManagedExecutable(layout, out var serverPath) &&
            await BundleLayoutRepairHelper.TryRepairAsync(bundleService, logger, "creating the AppHost server project", cancellationToken, layout?.LayoutPath).ConfigureAwait(false))
        {
            layout = await bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);
            HasUsableManagedExecutable(layout, out serverPath);
        }

        // Priority 3: Check if we have a bundle layout with a pre-built AppHost server
        if (layout is not null && serverPath is not null && File.Exists(serverPath))
        {
            logger.LogDebug("Using bundled AppHost server from {ServerPath}", serverPath);
            return new PrebuiltAppHostServer(
                appPath,
                socketPath,
                layout,
                bundleNuGetService,
                dotNetCliRunner,
                sdkInstaller,
                packagingService,
                configurationService,
                loggerFactory.CreateLogger<PrebuiltAppHostServer>());
        }

        logger.LogError("No usable Aspire AppHost server was found for {AppPath}. Repository mode was unavailable and the bundled layout did not provide a usable managed executable. {BundleState}",
            appPath,
            bundleService.GetLayoutState(layout?.LayoutPath).Describe());

        throw new InvalidOperationException(
            "No Aspire AppHost server is available. Ensure the Aspire CLI is installed " +
            "with a valid bundle layout, or reinstall using 'aspire setup --force'.");
    }

    private static bool HasUsableManagedExecutable(LayoutConfiguration? layout, out string? serverPath)
    {
        serverPath = layout?.GetManagedPath();
        return serverPath is not null && File.Exists(serverPath);
    }
}
