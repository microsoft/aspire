// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
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
    ILoggerFactory loggerFactory) : IAppHostServerProjectFactory
{
    public async Task<IAppHostServerProject> CreateAsync(string appPath, CancellationToken cancellationToken = default)
    {
        // Normalize the path
        var normalizedPath = Path.GetFullPath(appPath);
        normalizedPath = new Uri(normalizedPath).LocalPath;
        normalizedPath = OperatingSystem.IsWindows() ? normalizedPath.ToLowerInvariant() : normalizedPath;

        // Generate socket path based on app path hash (deterministic for same project)
        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var socketName = Convert.ToHexString(pathHash)[..12].ToLowerInvariant() + ".sock";

        string socketPath;
        if (OperatingSystem.IsWindows())
        {
            // Windows uses named pipes
            socketPath = socketName;
        }
        else
        {
            // Unix uses domain sockets
            var socketDir = Path.Combine(Path.GetTempPath(), ".aspire", "sockets");
            Directory.CreateDirectory(socketDir);
            socketPath = Path.Combine(socketDir, socketName);
        }

        // Priority 1: Check for dev mode (ASPIRE_REPO_ROOT or running from Aspire source repo)
        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot(appPath);
        if (repoRoot is not null)
        {
            return new DotNetBasedAppHostServerProject(
                appPath,
                socketPath,
                repoRoot,
                dotNetCliRunner,
                packagingService,
                configurationService,
                loggerFactory.CreateLogger<DotNetBasedAppHostServerProject>());
        }

        // Priority 2: Ensure bundle is extracted and check for layout
        var layout = await bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken);

        // Priority 3: Check if we have a bundle layout with a pre-built AppHost server
        if (layout is not null && layout.GetManagedPath() is string serverPath && File.Exists(serverPath))
        {
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

        throw new InvalidOperationException(
            "No Aspire AppHost server is available. Ensure the Aspire CLI is installed " +
            "with a valid bundle layout, or reinstall using 'aspire setup --force'.");
    }
}
