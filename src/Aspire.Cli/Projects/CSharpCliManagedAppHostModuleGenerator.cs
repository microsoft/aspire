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
    internal const string AppHostBuildPropsFileName = "AppHost.Directory.Build.props";
    internal const string AppHostBuildTargetsFileName = "AppHost.Directory.Build.targets";
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
        var appHostBuildPropsFile = new FileInfo(Path.Combine(modulesDirectory.FullName, AppHostBuildPropsFileName));
        var appHostBuildTargetsFile = new FileInfo(Path.Combine(modulesDirectory.FullName, AppHostBuildTargetsFileName));
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
        await WriteAppHostBuildPropsFileAsync(appHostBuildPropsFile, appHostFile, additionalSources, restoreSources.PackageSourceMappings is not null ? nuGetConfigFile : null, integrationReferences, repoRoot, cancellationToken).ConfigureAwait(false);
        await WriteAppHostBuildTargetsFileAsync(appHostBuildTargetsFile, appHostFile, cancellationToken).ConfigureAwait(false);
        if (legacyModuleTargetsFile.Exists)
        {
            legacyModuleTargetsFile.Delete();
        }

        // Directory.Build.props is where SDK-style projects require BaseIntermediateOutputPath
        // and MSBuildProjectExtensionsPath to be set; assigning them in Aspire.csproj is too late
        // because Microsoft.Common.props has already consumed them.
        await File.WriteAllTextAsync(
            Path.Combine(modulesDirectory.FullName, "Directory.Build.props"),
            IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(integrationRestoreDir).ToString(),
            cancellationToken).ConfigureAwait(false);

        // Write sentinel targets/packages files to prevent upstream imports from overriding generated project behavior.
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

    private static async Task WriteAppHostBuildPropsFileAsync(
        FileInfo appHostBuildPropsFile,
        FileInfo appHostFile,
        IReadOnlyList<string> additionalSources,
        FileInfo? restoreConfigFile,
        IReadOnlyList<IntegrationReference> integrationReferences,
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        var generatedProjectPath = Path.ChangeExtension(appHostFile.FullName, ".csproj");
        var projectCondition = $"'$(MSBuildProjectFullPath)' == '{generatedProjectPath}'";
        var appHostBuildDirectory = Path.Combine(appHostFile.Directory!.FullName, AspireJsonConfiguration.SettingsFolder, "build", "apphost");

        var root = new XElement("Project");
        var propertyGroup = new XElement("PropertyGroup", new XAttribute("Condition", projectCondition));
        propertyGroup.Add(new XElement("BaseOutputPath", CliPathHelper.EnsureTrailingSlash(Path.Combine(appHostBuildDirectory, "bin"))));
        propertyGroup.Add(new XElement("BaseIntermediateOutputPath", CliPathHelper.EnsureTrailingSlash(Path.Combine(appHostBuildDirectory, "obj"))));
        propertyGroup.Add(new XElement("MSBuildProjectExtensionsPath", "$(BaseIntermediateOutputPath)"));

        if (additionalSources.Count > 0)
        {
            propertyGroup.Add(new XElement("RestoreAdditionalProjectSources", string.Join(";", additionalSources)));
        }

        if (restoreConfigFile is not null)
        {
            propertyGroup.Add(new XElement("RestoreConfigFile", restoreConfigFile.FullName));
        }

        if (propertyGroup.HasElements)
        {
            root.Add(propertyGroup);
        }

        var projectFile = new CSharpProjectFile();
        projectFile.AddIntegrationReferences(
            integrationReferences,
            repoRoot,
            isAspireProjectResource: false,
            referenceOutputAssembly: true);

        if (projectFile.PackageReferences.Count > 0)
        {
            root.Add(new XElement("ItemGroup",
                new XAttribute("Condition", projectCondition),
                projectFile.PackageReferences.Select(CSharpProjectFile.CreatePackageReferenceElement)));
        }

        if (projectFile.ProjectReferences.Count > 0)
        {
            root.Add(new XElement("ItemGroup",
                new XAttribute("Condition", projectCondition),
                projectFile.ProjectReferences.Select(CSharpProjectFile.CreateProjectReferenceElement)));
        }

        await using var stream = appHostBuildPropsFile.Create();
        await new XDocument(root).SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAppHostBuildTargetsFileAsync(
        FileInfo appHostBuildTargetsFile,
        FileInfo appHostFile,
        CancellationToken cancellationToken)
    {
        var generatedProjectPath = Path.ChangeExtension(appHostFile.FullName, ".csproj");
        var projectCondition = $"'$(MSBuildProjectFullPath)' == '{generatedProjectPath}'";

        var root = new XElement("Project");
        root.Add(new XElement("ItemGroup",
            new XAttribute("Condition", projectCondition),
            new XElement("ProjectReference",
                new XAttribute("Update", "@(ProjectReference)"),
                new XAttribute("GlobalPropertiesToRemove", "%(ProjectReference.GlobalPropertiesToRemove);DirectoryBuildPropsPath;DirectoryBuildTargetsPath"))));

        await using var stream = appHostBuildTargetsFile.Create();
        await new XDocument(root).SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }

    internal static void AddBuildProperty(ProcessInvocationOptions options)
    {
        options.MSBuildProperties[BuildPropertyName] = "true";
        options.MSBuildProperties["JsonSerializerIsReflectionEnabledByDefault"] = "true";
    }

    internal static void AddAppHostBuildProperties(FileInfo appHostFile, ProcessInvocationOptions options)
    {
        AddBuildProperty(options);
        options.MSBuildProperties["DirectoryBuildPropsPath"] = GetAppHostBuildPropsFile(appHostFile).FullName;
        options.MSBuildProperties["DirectoryBuildTargetsPath"] = GetAppHostBuildTargetsFile(appHostFile).FullName;
    }

    internal static FileInfo GetAppHostBuildPropsFile(FileInfo appHostFile)
    {
        var appHostDirectory = appHostFile.Directory ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' does not have a containing directory.");
        return new FileInfo(Path.Combine(appHostDirectory.FullName, AspireJsonConfiguration.SettingsFolder, ModulesDirectoryName, AppHostBuildPropsFileName));
    }

    internal static FileInfo GetAppHostBuildTargetsFile(FileInfo appHostFile)
    {
        var appHostDirectory = appHostFile.Directory ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' does not have a containing directory.");
        return new FileInfo(Path.Combine(appHostDirectory.FullName, AspireJsonConfiguration.SettingsFolder, ModulesDirectoryName, AppHostBuildTargetsFileName));
    }

}
