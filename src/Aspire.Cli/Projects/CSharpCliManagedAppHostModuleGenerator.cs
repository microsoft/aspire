// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal interface ICSharpCliManagedAppHostModuleGenerator
{
    Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, CancellationToken cancellationToken);

    Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, AspireConfigFile config, DirectoryInfo configDirectory, string? packageSourceOverride, CancellationToken cancellationToken);
}

internal sealed class CSharpCliManagedAppHostModuleGenerator(
    IPackagingService packagingService,
    ILogger<CSharpCliManagedAppHostModuleGenerator> logger) : ICSharpCliManagedAppHostModuleGenerator
{
    internal const string ModulesDirectoryName = "modules";
    internal const string ModuleProjectFileName = "Aspire.csproj";
    internal const string NuGetConfigFileName = "nuget.config";
    internal const string BuildPropertyName = "AspireCliManagedAppHostBuild";

    public async Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        var appHostDirectory = appHostFile.Directory;
        if (appHostDirectory is null)
        {
            return null;
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var config = AspireConfigFile.Load(configDirectory.FullName) ?? new AspireConfigFile();
        return await TryGenerateAsync(appHostFile, config, configDirectory, packageSourceOverride: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, AspireConfigFile config, DirectoryInfo configDirectory, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        var appHostDirectory = appHostFile.Directory;
        if (appHostDirectory is null)
        {
            return null;
        }

        var modulesDirectory = new DirectoryInfo(Path.Combine(appHostDirectory.FullName, AspireJsonConfiguration.SettingsFolder, ModulesDirectoryName));
        modulesDirectory.Create();

        var moduleProjectFile = new FileInfo(Path.Combine(modulesDirectory.FullName, ModuleProjectFileName));
        var nuGetConfigFile = new FileInfo(Path.Combine(modulesDirectory.FullName, NuGetConfigFileName));
        var legacyModuleTargetsFile = new FileInfo(Path.Combine(modulesDirectory.FullName, "Aspire.targets"));

        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot(appHostDirectory.FullName);
        var integrationReferences = config
            .GetIntegrationReferences(DotNetBasedAppHostServerProject.DefaultSdkVersion, configDirectory.FullName)
            .ToList();
        var restoreSources = await ResolveRestoreSourcesAsync(config.Channel, packageSourceOverride, cancellationToken).ConfigureAwait(false);
        if (restoreSources.PackageSourceMappings is not null)
        {
            using var temporaryConfig = await TemporaryNuGetConfig.CreateAsync(
                restoreSources.PackageSourceMappings,
                restoreSources.ConfigureGlobalPackagesFolder).ConfigureAwait(false);
            File.Copy(temporaryConfig.ConfigFile.FullName, nuGetConfigFile.FullName, overwrite: true);
        }
        else if (nuGetConfigFile.Exists)
        {
            nuGetConfigFile.Delete();
        }

        var workingDirectory = IntegrationClosureRestorer.GetOrCreateWorkingDirectory(appHostFile);
        var integrationRestoreDir = Path.Combine(workingDirectory.FullName, IntegrationClosureRestorer.IntegrationRestoreFolderName);
        Directory.CreateDirectory(integrationRestoreDir);

        await WriteModuleProjectFileAsync(moduleProjectFile, restoreSources.AdditionalSources, restoreSources.PackageSourceMappings is not null ? nuGetConfigFile : null, integrationRestoreDir, integrationReferences, repoRoot, cancellationToken).ConfigureAwait(false);
        if (legacyModuleTargetsFile.Exists)
        {
            legacyModuleTargetsFile.Delete();
        }

        // Write sentinel files to prevent upstream props/targets from overriding generated project behavior.
        await File.WriteAllTextAsync(Path.Combine(modulesDirectory.FullName, "Directory.Build.props"), "<Project />", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(modulesDirectory.FullName, "Directory.Build.targets"), "<Project />", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(modulesDirectory.FullName, "Directory.Packages.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """,
            cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Generated CLI-managed C# AppHost module project at {ProjectPath}", moduleProjectFile.FullName);
        return moduleProjectFile;
    }

    private async Task<ModuleRestoreSources> ResolveRestoreSourcesAsync(string? configuredChannelName, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        var additionalSources = new List<string>();
        var safePackageSourceOverride = !string.IsNullOrWhiteSpace(packageSourceOverride) &&
            !PackageSourceOverrideMappings.HasCredentialMaterial(packageSourceOverride)
                ? packageSourceOverride
                : null;
        PackageMapping[]? packageSourceMappings = null;
        var configureGlobalPackagesFolder = false;

        ThrowIfStagingUnavailable(configuredChannelName);

        // Match PrebuiltAppHostServer's project-reference closure restore behavior: when
        // mappings can be persisted safely, RestoreConfigFile carries the source contract and
        // RestoreAdditionalProjectSources stays empty so mapped Aspire feeds don't become
        // co-eligible through both mechanisms.
        if (!string.IsNullOrWhiteSpace(safePackageSourceOverride))
        {
            additionalSources.Add(safePackageSourceOverride);
        }

        if (!string.IsNullOrWhiteSpace(safePackageSourceOverride) &&
            string.IsNullOrEmpty(configuredChannelName))
        {
            packageSourceMappings = PackageSourceOverrideMappings.Create(safePackageSourceOverride, requestedChannel: null);

            return new ModuleRestoreSources([], packageSourceMappings, configureGlobalPackagesFolder);
        }

        try
        {
            var channels = await packagingService.GetChannelsAsync(cancellationToken, configuredChannelName).ConfigureAwait(false);
            var hasOverride = !string.IsNullOrWhiteSpace(safePackageSourceOverride);
            var matchedChannels = !string.IsNullOrEmpty(configuredChannelName)
                ? channels.Where(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase))
                : !hasOverride
                    ? channels.Where(c => c.Type == PackageChannelType.Explicit)
                    : [];
            var matchedChannel = !string.IsNullOrEmpty(configuredChannelName)
                ? matchedChannels.FirstOrDefault(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (hasOverride)
            {
                packageSourceMappings = PackageSourceOverrideMappings.Create(safePackageSourceOverride!, matchedChannel);
                configureGlobalPackagesFolder = matchedChannel?.ConfigureGlobalPackagesFolder == true;
            }
            else if (matchedChannel?.Mappings is { Length: > 0 } &&
                !string.Equals(matchedChannel.Name, PackageChannelNames.Local, StringComparisons.ChannelName))
            {
                packageSourceMappings = matchedChannel.Mappings;
                configureGlobalPackagesFolder = matchedChannel.ConfigureGlobalPackagesFolder;
            }

            foreach (var channel in matchedChannels)
            {
                if (channel.Mappings is null)
                {
                    continue;
                }

                foreach (var mapping in channel.Mappings)
                {
                    if (hasOverride && mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!additionalSources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        additionalSources.Add(mapping.Source);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve package channels while generating CLI-managed C# AppHost module.");
        }

        return new ModuleRestoreSources(packageSourceMappings is null ? additionalSources : [], packageSourceMappings, configureGlobalPackagesFolder);
    }

    private void ThrowIfStagingUnavailable(string? configuredChannelName)
    {
        if (!string.Equals(configuredChannelName, PackageChannelNames.Staging, StringComparisons.ChannelName))
        {
            return;
        }

        var reason = packagingService.GetStagingChannelUnavailableReason();
        if (reason is not null)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static async Task WriteModuleProjectFileAsync(
        FileInfo moduleProjectFile,
        IReadOnlyList<string> additionalSources,
        FileInfo? restoreConfigFile,
        string integrationRestoreDir,
        IReadOnlyList<IntegrationReference> integrationReferences,
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            integrationRestoreDir,
            additionalSources,
            restoreConfigFile?.FullName);

        projectFile.AddIntegrationReferences(
            integrationReferences,
            repoRoot,
            isAspireProjectResource: false,
            referenceOutputAssembly: true);
        projectFile.AddRepositoryProjectReferenceIfExists(
            repoRoot,
            "Aspire.Dashboard",
            isAspireProjectResource: false,
            referenceOutputAssembly: false,
            privateReference: false);

        projectFile.Targets.Add(
            new XElement("Target",
                new XAttribute("Name", "FailDirectDotnetForCliManagedAppHost"),
                new XAttribute("BeforeTargets", "Build;Publish"),
                new XAttribute("Condition", $"'$({BuildPropertyName})' != 'true'"),
                new XElement("Error", new XAttribute("Text", "This AppHost is managed by the Aspire CLI. Use 'aspire run', 'aspire restore', or 'aspire publish' instead of direct dotnet commands."))));

        await using var stream = moduleProjectFile.Create();
        await projectFile.ToXDocument().SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }

    internal static void AddBuildProperty(ProcessInvocationOptions options)
    {
        options.MSBuildProperties[BuildPropertyName] = "true";
        options.MSBuildProperties["JsonSerializerIsReflectionEnabledByDefault"] = "true";
    }

    private sealed record ModuleRestoreSources(
        IReadOnlyList<string> AdditionalSources,
        PackageMapping[]? PackageSourceMappings,
        bool ConfigureGlobalPackagesFolder);

}
