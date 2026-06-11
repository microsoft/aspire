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
    IFeatures features,
    IPackagingService packagingService,
    ILogger<CSharpCliManagedAppHostModuleGenerator> logger) : ICSharpCliManagedAppHostModuleGenerator
{
    internal const string ModulesDirectoryName = "modules";
    internal const string ModuleProjectFileName = "Aspire.csproj";
    internal const string NuGetConfigFileName = "nuget.config";
    internal const string BuildPropertyName = "AspireCliManagedAppHostBuild";

    public async Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (!DotNetAppHostProject.IsCliManagedSingleFileAppHost(appHostFile, features))
        {
            return null;
        }

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
        if (!DotNetAppHostProject.IsCliManagedSingleFileAppHost(appHostFile, features))
        {
            return null;
        }

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

        // Match DotNetBasedAppHostServerProject: source overrides are a best-effort first
        // source for SDK-driven restore. Match PrebuiltAppHostServer's package-source-mapping
        // behavior when it is safe to persist a generated nuget.config under .aspire/modules.
        if (!string.IsNullOrWhiteSpace(safePackageSourceOverride))
        {
            additionalSources.Add(safePackageSourceOverride);
        }

        try
        {
            var channels = await packagingService.GetChannelsAsync(cancellationToken, configuredChannelName).ConfigureAwait(false);
            var matchedChannels = !string.IsNullOrEmpty(configuredChannelName)
                ? channels.Where(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase))
                : string.IsNullOrWhiteSpace(safePackageSourceOverride)
                    ? channels.Where(c => c.Type == PackageChannelType.Explicit)
                    : [];
            var matchedChannel = !string.IsNullOrEmpty(configuredChannelName)
                ? matchedChannels.FirstOrDefault(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (!string.IsNullOrWhiteSpace(safePackageSourceOverride))
            {
                packageSourceMappings = PackageSourceOverrideMappings.Create(safePackageSourceOverride, matchedChannel);
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

        return new ModuleRestoreSources(additionalSources, packageSourceMappings, configureGlobalPackagesFolder);
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
        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", DotNetBasedAppHostServerProject.TargetFramework),
            new XElement("EnableDefaultItems", "false"),
            new XElement("ImplicitUsings", "enable"),
            new XElement("Nullable", "enable"),
            new XElement("IsPackable", "false"),
            new XElement("IsPublishable", "false"));

        // The closure-manifest properties point into .aspire/integrations/apphosts/<hash>/integration-restore/,
        // mirroring the PrebuiltAppHostServer cache layout. AfterBuild targets
        // write the closure files to those paths so IntegrationClosureRestorer can read them and emit
        // the probe manifest the runtime AppHost consumes.
        IntegrationClosureRestorer.AddClosureProperties(propertyGroup, integrationRestoreDir);

        if (additionalSources.Count > 0)
        {
            propertyGroup.Add(new XElement("RestoreAdditionalProjectSources", string.Join(";", additionalSources)));
        }
        if (restoreConfigFile is not null)
        {
            propertyGroup.Add(new XElement("RestoreConfigFile", restoreConfigFile.FullName));
        }

        var doc = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                propertyGroup));

        var itemGroup = new XElement("ItemGroup");
        // privateProjectReferences:false: this module exists specifically to harvest the
        // integration closure via ReferenceCopyLocalPaths. Setting Private=false would drop
        // in-repo project-ref output assemblies (e.g. Aspire.Hosting.Redis from src/) from
        // the closure files and leave the runtime AppHost unable to load them.
        var resolvedReferences = CSharpIntegrationProjectReferences.Resolve(integrationReferences, repoRoot, privateProjectReferences: false);
        itemGroup.Add(resolvedReferences.ProjectReferences);
        itemGroup.Add(resolvedReferences.PackageReferences);
        if (TryGetRepositoryProject(repoRoot, "Aspire.Dashboard", out var dashboardProjectPath))
        {
            itemGroup.Add(CreateBuildOnlyProjectReferenceElement(dashboardProjectPath));
        }

        if (itemGroup.HasElements)
        {
            doc.Root!.Add(itemGroup);
        }

        doc.Root!.Add(
            new XElement("Target",
                new XAttribute("Name", "FailDirectDotnetForCliManagedAppHost"),
                new XAttribute("BeforeTargets", "Build;Publish"),
                new XAttribute("Condition", $"'$({BuildPropertyName})' != 'true'"),
                new XElement("Error", new XAttribute("Text", "This AppHost is managed by the Aspire CLI. Use 'aspire run', 'aspire restore', or 'aspire publish' instead of direct dotnet commands."))));

        // Emit the same closure-writing MSBuild targets the polyglot PrebuiltAppHostServer uses
        // for its synthetic IntegrationRestore.csproj. The CLI builds Aspire.csproj with
        // AspireCliManagedAppHostBuild=true; these targets then write the closure file set into
        // .aspire/integrations/apphosts/<hash>/integration-restore/ so IntegrationClosureRestorer
        // can post-process them into a stable libs layout + IntegrationPackageProbeManifest.
        IntegrationClosureRestorer.AddClosureTargets(doc.Root!);

        await using var stream = moduleProjectFile.Create();
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
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

    private static bool TryGetRepositoryProject(string? repoRoot, string projectName, out string projectPath)
    {
        projectPath = null!;
        if (repoRoot is null)
        {
            return false;
        }

        var candidatePath = Path.Combine(repoRoot, "src", projectName, $"{projectName}.csproj");
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        projectPath = candidatePath;
        return true;
    }

    private static XElement CreateBuildOnlyProjectReferenceElement(string projectPath)
    {
        return new XElement("ProjectReference",
            new XAttribute("Include", projectPath),
            new XElement("IsAspireProjectResource", "false"),
            new XElement("ReferenceOutputAssembly", "false"),
            new XElement("Private", "false"));
    }
}
