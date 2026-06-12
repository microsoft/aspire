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
        var restoreSources = await new IntegrationRestoreSourceResolver(packagingService, logger)
            .ResolveAsync(config.Channel, packageSourceOverride, cancellationToken)
            .ConfigureAwait(false);
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
        var integrationRestoreDir = Path.Combine(workingDirectory.FullName, IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Directory.CreateDirectory(integrationRestoreDir);

        IReadOnlyList<string> additionalSources = restoreSources.PackageSourceMappings is null
            ? restoreSources.AdditionalSources
            : [];
        await WriteModuleProjectFileAsync(moduleProjectFile, additionalSources, restoreSources.PackageSourceMappings is not null ? nuGetConfigFile : null, integrationRestoreDir, integrationReferences, repoRoot, cancellationToken).ConfigureAwait(false);
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

}
