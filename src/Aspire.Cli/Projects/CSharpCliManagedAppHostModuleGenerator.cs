// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal sealed class CSharpCliManagedAppHostModuleGenerator(
    IPackagingService packagingService,
    BundleNuGetService nugetService,
    CliExecutionContext executionContext,
    ILogger<CSharpCliManagedAppHostModuleGenerator> logger)
{
    private readonly IntegrationRestorePlanResolver _restorePlanResolver = new(
        packagingService,
        nugetService,
        executionContext,
        logger);
    private readonly string _identitySdkVersion = executionContext.IdentitySdkVersion;

    internal const string ModulesDirectoryName = "modules";
    internal const string ModuleProjectFileName = "Aspire.csproj";
    internal const string AppHostBuildPropsFileName = "AppHost.Directory.Build.props";
    internal const string AppHostBuildTargetsFileName = "AppHost.Directory.Build.targets";
    internal const string NuGetConfigFileName = "nuget.config";
    internal const string BuildPropertyName = "AspireCliManagedAppHostBuild";

    internal async Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, CancellationToken cancellationToken)
        => (await TryGenerateWithRestoreConfigurationAsync(appHostFile, cancellationToken).ConfigureAwait(false))?.ModuleProjectFile;

    internal async Task<CliManagedAppHostModuleGenerationResult?> TryGenerateWithRestoreConfigurationAsync(
        FileInfo appHostFile,
        CancellationToken cancellationToken)
    {
        var appHostDirectory = appHostFile.Directory;
        if (appHostDirectory is null)
        {
            return null;
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var config = AspireConfigFile.Load(configDirectory.FullName) ?? new AspireConfigFile();
        return await GenerateAsync(appHostFile, config, configDirectory, config.Channel, packageSourceOverride: null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileInfo?> TryGenerateAsync(FileInfo appHostFile, AspireConfigFile config, DirectoryInfo configDirectory, string? packageSourceOverride, CancellationToken cancellationToken)
        => (await GenerateAsync(appHostFile, config, configDirectory, config.Channel, packageSourceOverride, cancellationToken).ConfigureAwait(false))?.ModuleProjectFile;

    internal Task<CliManagedAppHostModuleGenerationResult?> TryGenerateWithRestoreConfigurationAsync(
        FileInfo appHostFile,
        AspireConfigFile config,
        DirectoryInfo configDirectory,
        string? packageSourceOverride,
        CancellationToken cancellationToken)
        => GenerateAsync(appHostFile, config, configDirectory, config.Channel, packageSourceOverride, cancellationToken);

    internal Task<CliManagedAppHostModuleGenerationResult?> TryGenerateWithRestoreConfigurationAsync(
        FileInfo appHostFile,
        AspireConfigFile config,
        DirectoryInfo configDirectory,
        string? restoreChannel,
        string? packageSourceOverride,
        CancellationToken cancellationToken)
        => GenerateAsync(appHostFile, config, configDirectory, restoreChannel, packageSourceOverride, cancellationToken);

    private async Task<CliManagedAppHostModuleGenerationResult?> GenerateAsync(
        FileInfo appHostFile,
        AspireConfigFile config,
        DirectoryInfo configDirectory,
        string? restoreChannel,
        string? packageSourceOverride,
        CancellationToken cancellationToken)
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
        var sdkVersion = config.SdkVersion ?? _identitySdkVersion;
        var integrationReferences = config
            .GetIntegrationReferences(sdkVersion, configDirectory.FullName)
            .ToList();
        var restorePlan = await _restorePlanResolver.ResolveAsync(
            appHostDirectory.FullName,
            sdkVersion,
            restoreChannel,
            packageSourceOverride,
            packageSourceOverridePattern: null,
            cancellationToken)
            .ConfigureAwait(false);
        var policyDirectory = IntegrationClosureBuilder.GetAppHostIntegrationPolicyDirectory(appHostDirectory);
        var restoreConfiguration = await restorePlan.ApplyProjectRestoreConfigurationAsync(
            policyDirectory,
            cancellationToken).ConfigureAwait(false);
        if (nuGetConfigFile.Exists)
        {
            nuGetConfigFile.Delete();
        }

        var workingDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostDirectory);
        var integrationRestoreDir = Path.Combine(workingDirectory.FullName, IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Directory.CreateDirectory(integrationRestoreDir);

        await WriteModuleProjectFileAsync(
            moduleProjectFile,
            restoreConfiguration.RootAdditionalSources,
            integrationRestoreDir,
            integrationReferences,
            repoRoot,
            cancellationToken).ConfigureAwait(false);
        await WriteAppHostBuildPropsFileAsync(
            appHostBuildPropsFile,
            appHostFile,
            restoreConfiguration,
            integrationReferences,
            repoRoot,
            cancellationToken).ConfigureAwait(false);
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
            IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(
                integrationRestoreDir,
                Path.Combine(integrationRestoreDir, "obj"),
                restoreConfiguration.RestoreRootConfigDirectory,
                restoreConfiguration.GlobalPackagesFolder).ToString(),
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
        return new CliManagedAppHostModuleGenerationResult(
            moduleProjectFile,
            IntegrationClosureBuilder.CreateRestoreAdditionalProjectSourcesValue(
                existingValue: null,
                restoreConfiguration.PackageSourceHints),
            restoreConfiguration.SensitiveSources,
            restoreConfiguration.GlobalPackagesFolder);
    }

    private static async Task WriteModuleProjectFileAsync(
        FileInfo moduleProjectFile,
        IReadOnlyList<string> additionalSources,
        string integrationRestoreDir,
        IReadOnlyList<IntegrationReference> integrationReferences,
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            integrationRestoreDir,
            additionalSources);

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
                new XAttribute("BeforeTargets", "Build;Publish;Restore"),
                new XAttribute("Condition", $"'$({BuildPropertyName})' != 'true' and '$(DesignTimeBuild)' != 'true' and '$(BuildingInsideVisualStudio)' != 'true'"),
                new XElement("Error", new XAttribute("Text", "This AppHost is managed by the Aspire CLI. Use 'aspire run', 'aspire restore', or 'aspire publish' instead of direct dotnet commands."))));

        await using var stream = moduleProjectFile.Create();
        await projectFile.ToXDocument().SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAppHostBuildPropsFileAsync(
        FileInfo appHostBuildPropsFile,
        FileInfo appHostFile,
        IntegrationProjectRestoreConfiguration restoreConfiguration,
        IReadOnlyList<IntegrationReference> integrationReferences,
        string? repoRoot,
        CancellationToken cancellationToken)
    {
        var generatedProjectPath = Path.ChangeExtension(appHostFile.FullName, ".csproj");
        var projectCondition = $"\"$(MSBuildProjectFullPath)\" == \"{CliPathHelper.EscapeMSBuildConditionStringLiteral(generatedProjectPath)}\"";
        var appHostBuildDirectory = Path.Combine(appHostFile.Directory!.FullName, AspireJsonConfiguration.SettingsFolder, "build", "apphost");

        var root = new XElement("Project");
        var propertyGroup = new XElement("PropertyGroup", new XAttribute("Condition", projectCondition));
        propertyGroup.Add(new XElement("BaseOutputPath", CliPathHelper.EnsureTrailingSlash(Path.Combine(appHostBuildDirectory, "bin"))));
        propertyGroup.Add(new XElement("BaseIntermediateOutputPath", CliPathHelper.EnsureTrailingSlash(Path.Combine(appHostBuildDirectory, "obj"))));
        propertyGroup.Add(new XElement("MSBuildProjectExtensionsPath", "$(BaseIntermediateOutputPath)"));
        propertyGroup.Add(new XElement("ManagePackageVersionsCentrally", "false"));
        propertyGroup.Add(new XElement("CentralPackageTransitivePinningEnabled", "false"));

        if (restoreConfiguration.RootAdditionalSources.Length > 0)
        {
            propertyGroup.Add(new XElement(
                "RestoreAdditionalProjectSources",
                string.Join(";", restoreConfiguration.RootAdditionalSources)));
        }

        propertyGroup.Add(new XElement(
            "RestoreRootConfigDirectory",
            restoreConfiguration.RestoreRootConfigDirectory));
        propertyGroup.Add(new XElement("RestoreConfigFile", string.Empty));

        if (restoreConfiguration.GlobalPackagesFolder is not null)
        {
            propertyGroup.Add(new XElement(
                "RestorePackagesPath",
                restoreConfiguration.GlobalPackagesFolder));
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
        var projectCondition = $"\"$(MSBuildProjectFullPath)\" == \"{CliPathHelper.EscapeMSBuildConditionStringLiteral(generatedProjectPath)}\"";

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

        // File-based AppHosts compiled by `dotnet run --file` do not import the
        // Aspire.Hosting.AppHost SDK targets that normally enable reflection-based
        // System.Text.Json serialization. In .NET 10, reflection-based serialization
        // is disabled by default, which breaks Aspire.Hosting runtime serialization
        // for the resource model and backchannel. Playground file-based AppHosts use
        // `#:property JsonSerializerIsReflectionEnabledByDefault=true`; CLI-managed
        // AppHosts need the equivalent property injected here because they bypass the
        // SDK directive.
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

internal sealed record CliManagedAppHostModuleGenerationResult(
    FileInfo ModuleProjectFile,
    string? IntegrationPackageSources,
    string[] SensitiveSources,
    string? GlobalPackagesFolder);
