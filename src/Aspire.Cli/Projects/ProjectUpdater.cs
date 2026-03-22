// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal interface IProjectUpdater
{
    Task<ProjectUpdateResult> UpdateProjectAsync(FileInfo projectFile, PackageChannel channel, CancellationToken cancellationToken = default);
}

internal sealed partial class ProjectUpdater(ILogger<ProjectUpdater> logger, IDotNetCliRunner runner, IInteractionService interactionService, IMemoryCache cache, CliExecutionContext executionContext, FallbackProjectParser fallbackParser) : IProjectUpdater
{
    public async Task<ProjectUpdateResult> UpdateProjectAsync(FileInfo projectFile, PackageChannel channel, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Fetching '{AppHostPath}' items and properties.", projectFile.FullName);

        var (updateSteps, fallbackUsed) = await interactionService.ShowStatusAsync(UpdateCommandStrings.AnalyzingProjectStatus, () => GetUpdateStepsAsync(projectFile, channel, cancellationToken));

        if (!updateSteps.Any())
        {
            logger.LogInformation("No updates required for project: {ProjectFile}", projectFile.FullName);
            interactionService.DisplayMessage(KnownEmojis.CheckMark, UpdateCommandStrings.ProjectUpToDateMessage);
            return new ProjectUpdateResult { UpdatedApplied = false };
        }

        interactionService.DisplayEmptyLine();

        // Group update steps by project for better visual organization
        var updateStepsByProject = updateSteps
            .OfType<PackageUpdateStep>()
            .GroupBy(step => step.ProjectFile.FullName)
            .ToList();

        // Display package updates grouped by project
        foreach (var projectGroup in updateStepsByProject)
        {
            var projectName = new FileInfo(projectGroup.Key).Name;
            if (updateStepsByProject.Count > 1)
            {
                interactionService.DisplayMessage(KnownEmojis.FileFolder, $"[bold cyan]{projectName.EscapeMarkup()}[/]:", allowMarkup: true);
            }

            foreach (var packageStep in projectGroup)
            {
                interactionService.DisplayMessage(KnownEmojis.Package, packageStep.GetFormattedDisplayText(), allowMarkup: true);
            }

            interactionService.DisplayEmptyLine();
        }

        // Display warning if fallback parsing was used
        if (fallbackUsed)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, $"[yellow]{UpdateCommandStrings.FallbackParsingWarning}[/]", allowMarkup: true);
            interactionService.DisplayEmptyLine();
        }

        if (!await interactionService.ConfirmAsync(UpdateCommandStrings.PerformUpdatesPrompt, true, cancellationToken))
        {
            return new ProjectUpdateResult { UpdatedApplied = false };
        }

        if (channel.Type == PackageChannelType.Explicit)
        {
            var (configPathsExitCode, configPaths) = await runner.GetNuGetConfigPathsAsync(projectFile.Directory!, new(), cancellationToken);

            if (configPathsExitCode != 0 || configPaths is null || configPaths.Length == 0)
            {
                throw new ProjectUpdaterException(UpdateCommandStrings.FailedDiscoverNuGetConfig);
            }

            var configPathDirectories = configPaths.Select(Path.GetDirectoryName).ToArray();
            var fallbackNuGetConfigDirectory = executionContext.WorkingDirectory.FullName;

            // If there is one or zero config paths we assume that we should use
            // the fallback (there should always be one, but just for exhaustivenss).
            // If there is more than one we just make sure that the first on in the list
            // isn't a global config (on Windows with .NET and VS installed you'll have 3
            // global config files but the first one should be the NuGet in AppData).
            // The final rule should never ever be invoked, its just to get around CS8846
            // which does not evaluate when statements for exhaustiveness.
            var recommendedNuGetConfigFileDirectory = configPathDirectories switch
            {
                { Length: 0 or 1 } => fallbackNuGetConfigDirectory,
                var p when p.Length > 1 => IsGlobalNuGetConfig(p[0]!) ? fallbackNuGetConfigDirectory : p[0],

                // CS8846 error if we don't put this rule here even though we do "when"
                // above - this is corner case in C# evalutation of switch statements.
                _ => throw new InvalidOperationException(UpdateCommandStrings.UnexpectedCodePath)
            };

            interactionService.DisplayEmptyLine();

            var selectedPathForNewNuGetConfigFile = await interactionService.PromptForFilePathAsync(
                promptText: UpdateCommandStrings.WhichDirectoryNuGetConfigPrompt,
                defaultValue: recommendedNuGetConfigFileDirectory.EscapeMarkup(),
                validator: null,
                directory: true,
                required: true,
                cancellationToken: cancellationToken);

            var nugetConfigDirectory = new DirectoryInfo(selectedPathForNewNuGetConfigFile);
            await NuGetConfigMerger.CreateOrUpdateAsync(nugetConfigDirectory, channel, AnalyzeAndConfirmNuGetConfigChanges, cancellationToken);
        }

        interactionService.DisplayEmptyLine();

        await interactionService.ShowStatusAsync(
            UpdateCommandStrings.ApplyingUpdates,
            async () =>
            {
                foreach (var updateStep in updateSteps)
                {
                    interactionService.DisplaySubtleMessage(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.ExecutingUpdateStepFormat, updateStep.Description));
                    await updateStep.Callback();
                }

                return 0;
            });

        interactionService.DisplayEmptyLine();

        interactionService.DisplaySuccess(UpdateCommandStrings.UpdateSuccessfulMessage);
        return new ProjectUpdateResult { UpdatedApplied = true };
    }

    private static bool IsGlobalNuGetConfig(string path)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            return path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }
        else
        {
            var globalNuGetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget");
            return path.StartsWith(globalNuGetFolder);
        }
    }

    private async Task<(IEnumerable<UpdateStep> UpdateSteps, bool FallbackUsed)> GetUpdateStepsAsync(FileInfo projectFile, PackageChannel channel, CancellationToken cancellationToken)
    {
        var context = new UpdateContext(projectFile, channel);

        var appHostAnalyzeStep = new AnalyzeStep(UpdateCommandStrings.AnalyzeAppHost, () => AnalyzeAppHostAsync(context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostAnalyzeStep);

        while (context.AnalyzeSteps.TryDequeue(out var analyzeStep))
        {
            await analyzeStep.Callback();
        }

        return (context.UpdateSteps, context.FallbackParsing);
    }

    private const string ItemsAndPropertiesCacheKeyPrefix = "ItemsAndProperties";

    private async Task<JsonDocument> GetItemsAndPropertiesAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        return await GetItemsAndPropertiesAsync(projectFile, ["PackageReference", "ProjectReference"], ["AspireHostingSDKVersion", "ManagePackageVersionsCentrally"], cancellationToken);
    }

    private async Task<JsonDocument> GetItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, CancellationToken cancellationToken)
    {
        // Create a cache key that includes the project file and the requested items/properties
        var itemsKey = string.Join(",", items.OrderBy(x => x));
        var propertiesKey = string.Join(",", properties.OrderBy(x => x));
        var cacheKey = $"{ItemsAndPropertiesCacheKeyPrefix}_{projectFile.FullName}_{itemsKey}_{propertiesKey}";

        var (exitCode, document) = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            return await runner.GetProjectItemsAndPropertiesAsync(projectFile, items, properties, new(), cancellationToken);
        });

        if (exitCode != 0 || document is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.FailedFetchItemsAndPropertiesFormat, projectFile.FullName));
        }

        return document;
    }

    private async Task<JsonDocument> GetItemsAndPropertiesWithFallbackAsync(FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        return await GetItemsAndPropertiesWithFallbackAsync(projectFile, ["PackageReference", "ProjectReference"], ["AspireHostingSDKVersion", "ManagePackageVersionsCentrally"], context, cancellationToken);
    }

    private async Task<JsonDocument> GetItemsAndPropertiesWithFallbackAsync(FileInfo projectFile, string[] items, string[] properties, UpdateContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Try normal MSBuild evaluation first
            return await GetItemsAndPropertiesAsync(projectFile, items, properties, cancellationToken);
        }
        catch (ProjectUpdaterException ex) when (IsAppHostProject(projectFile, context))
        {
            // Only use fallback for AppHost projects
            logger.LogWarning("Falling back to parsing for '{ProjectFile}'. Reason: {Message}", projectFile.FullName, ex.Message);

            if (!context.FallbackParsing)
            {
                context.FallbackParsing = true;
                logger.LogWarning("Update plan will be generated using fallback parsing; dependency accuracy may be reduced.");
            }

            return fallbackParser.ParseProject(projectFile);
        }
    }

    private static bool IsAppHostProject(FileInfo projectFile, UpdateContext context)
    {
        return string.Equals(projectFile.FullName, context.AppHostProjectFile.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private Task AnalyzeAppHostAsync(UpdateContext context, CancellationToken cancellationToken)
    {
        var appHostSdkAnalyzeStep = new AnalyzeStep(UpdateCommandStrings.AnalyzeAppHostSdk, () => AnalyzeAppHostSdkAsync(context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostSdkAnalyzeStep);

        var appHostProjectAnalyzeStep = new AnalyzeStep(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AnalyzeProjectFormat, context.AppHostProjectFile.FullName), () => AnalyzeProjectAsync(context.AppHostProjectFile, context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostProjectAnalyzeStep);

        return Task.CompletedTask;
    }

    private async Task<NuGetPackageCli> GetLatestVersionOfPackageAsync(UpdateContext context, string packageId, CancellationToken cancellationToken)
    {
        var cacheKey = $"LatestPackage-{packageId}";
        var latestPackage = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var packages = await context.Channel.GetPackagesAsync(packageId, context.AppHostProjectFile.Directory!, cancellationToken);
            // Filter out packages with invalid semantic versions and find the latest valid one
            var latestPackage = packages
                .Where(p => SemVersion.TryParse(p.Version, SemVersionStyles.Strict, out _))
                .OrderByDescending(p => SemVersion.Parse(p.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                .FirstOrDefault();
            return latestPackage;
        });

        return latestPackage ?? throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.NoPackageFoundFormat, packageId, context.Channel.Name));
    }

    private async Task AnalyzeAppHostSdkAsync(UpdateContext context, CancellationToken cancellationToken)
    {
        logger.LogDebug("Analyzing App Host SDK for: {AppHostFile}", context.AppHostProjectFile.FullName);

        var itemsAndPropertiesDocument = await GetItemsAndPropertiesWithFallbackAsync(context.AppHostProjectFile, context, cancellationToken);
        var propertiesElement = itemsAndPropertiesDocument.RootElement.GetProperty("Properties");
        var sdkVersionElement = propertiesElement.GetProperty("AspireHostingSDKVersion");
        var sdkVersion = sdkVersionElement.GetString();

        var latestSdkPackage = await GetLatestVersionOfPackageAsync(context, "Aspire.AppHost.Sdk", cancellationToken);

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (!string.IsNullOrEmpty(sdkVersion) && IsValidSemanticVersion(sdkVersion) && sdkVersion == latestSdkPackage?.Version)
        {
            logger.LogInformation("App Host SDK is up to date.");
            return;
        }

        // Detect what migration actions will be performed for .csproj files
        var migrationInfo = DetectMigrationActions(context.AppHostProjectFile);

        var sdkUpdateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, "Aspire.AppHost.Sdk", sdkVersion ?? "unknown", latestSdkPackage?.Version),
            () => UpdateSdkVersionInAppHostAsync(context.AppHostProjectFile, latestSdkPackage!, interactionService, migrationInfo),
            "Aspire.AppHost.Sdk",
            sdkVersion ?? "unknown",
            latestSdkPackage?.Version ?? "unknown",
            context.AppHostProjectFile);
        context.UpdateSteps.Enqueue(sdkUpdateStep);
    }

    private static SdkMigrationInfo DetectMigrationActions(FileInfo projectFile)
    {
        if (!string.Equals(projectFile.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new SdkMigrationInfo(false, false);
        }

        try
        {
            var projectDocument = new XmlDocument();
            projectDocument.PreserveWhitespace = true;
            projectDocument.Load(projectFile.FullName);

            var projectNode = projectDocument.SelectSingleNode("/Project");
            if (projectNode is null)
            {
                return new SdkMigrationInfo(false, false);
            }

            // Check if using old SDK format (needs migration to new format)
            var sdkAttribute = projectNode.Attributes?["Sdk"];
            var usesOldFormat = sdkAttribute is null || !ContainsAspireAppHostSdk(sdkAttribute.Value);

            // Check if Aspire.Hosting.AppHost package reference exists (will be removed)
            var hasAppHostPackage = projectNode.SelectSingleNode("//PackageReference[@Include='Aspire.Hosting.AppHost']") is not null;

            return new SdkMigrationInfo(usesOldFormat, hasAppHostPackage);
        }
        catch
        {
            return new SdkMigrationInfo(false, false);
        }
    }

    private const string AspireAppHostSdkName = "Aspire.AppHost.Sdk";

    /// <summary>
    /// Checks if the Sdk attribute contains the Aspire.AppHost.Sdk.
    /// Handles formats like "Aspire.AppHost.Sdk/13.0.1" or "Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk".
    /// </summary>
    private static bool ContainsAspireAppHostSdk(string sdkAttribute)
    {
        var sdks = sdkAttribute.Split(';');
        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            // Check for exact match "Aspire.AppHost.Sdk" or "Aspire.AppHost.Sdk/version"
            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates the Aspire.AppHost.Sdk version in the Sdk attribute, preserving any other SDKs.
    /// </summary>
    private static string UpdateAspireAppHostSdkVersion(string sdkAttribute, string newVersion)
    {
        var sdks = sdkAttribute.Split(';');
        var updatedSdks = new List<string>();

        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            // Check if this is the Aspire.AppHost.Sdk
            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                // Replace with new version
                updatedSdks.Add($"{AspireAppHostSdkName}/{newVersion}");
            }
            else
            {
                updatedSdks.Add(trimmedSdk);
            }
        }

        return string.Join(";", updatedSdks);
    }

    internal static async Task UpdateSdkVersionInAppHostAsync(FileInfo projectFile, NuGetPackageCli package, IInteractionService interactionService, SdkMigrationInfo migrationInfo)
    {
        if (string.Equals(projectFile.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSdkVersionInCsprojAppHostAsync(projectFile, package);

            // Display migration feedback messages
            if (migrationInfo.WillMigrateToNewFormat)
            {
                interactionService.DisplaySubtleMessage(string.Format(CultureInfo.InvariantCulture,
                    UpdateCommandStrings.MigratedToNewSdkFormat, package.Version));
            }

            if (migrationInfo.WillRemoveAppHostPackage)
            {
                interactionService.DisplaySubtleMessage(UpdateCommandStrings.RemovedObsoleteAppHostPackage);
            }
        }
        else if (string.Equals(projectFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSdkVersionInSingleFileAppHostAsync(projectFile, package);
        }
        else
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                "Unsupported AppHost file type: {0}. Expected .csproj or .cs file.", projectFile.Extension));
        }
    }

    internal static async Task UpdateSdkVersionInCsprojAppHostAsync(FileInfo projectFile, NuGetPackageCli package)
    {
        var projectDocument = new XmlDocument();
        projectDocument.PreserveWhitespace = true;

        projectDocument.Load(projectFile.FullName);

        var projectNode = projectDocument.SelectSingleNode("/Project");
        if (projectNode is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindRootProjectElementFormat, projectFile.FullName));
        }

        // Check if the SDK is set via the Sdk attribute on the Project element (new format)
        var sdkAttribute = projectNode.Attributes?["Sdk"];
        if (sdkAttribute is not null && ContainsAspireAppHostSdk(sdkAttribute.Value))
        {
            // Already using new format: <Project Sdk="Aspire.AppHost.Sdk/version">
            // Update the version, preserving any other SDKs in the attribute
            sdkAttribute.Value = UpdateAspireAppHostSdkVersion(sdkAttribute.Value, package.Version);
        }
        else
        {
            // Migrate from old format to new format
            // Old format: <Sdk Name="Aspire.AppHost.Sdk" Version="..." />
            var sdkNode = projectNode.SelectSingleNode("Sdk[@Name='Aspire.AppHost.Sdk']");
            if (sdkNode is null)
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindSdkElementFormat, projectFile.FullName));
            }

            // Set the new format: <Project Sdk="Aspire.AppHost.Sdk/version">
            // The Aspire.AppHost.Sdk already includes the base .NET SDK, so we replace any existing SDK attribute
            if (sdkAttribute is not null)
            {
                sdkAttribute.Value = $"{AspireAppHostSdkName}/{package.Version}";
            }
            else
            {
                var newSdkAttribute = projectDocument.CreateAttribute("Sdk");
                newSdkAttribute.Value = $"{AspireAppHostSdkName}/{package.Version}";
                projectNode.Attributes!.SetNamedItem(newSdkAttribute);
            }

            // Remove the old <Sdk Name="Aspire.AppHost.Sdk" /> element and any surrounding whitespace
            RemoveNodeWithWhitespace(sdkNode);
        }

        // Remove the Aspire.Hosting.AppHost package reference if present (no longer needed with new SDK format)
        RemovePackageReference(projectNode, "Aspire.Hosting.AppHost");

        projectDocument.Save(projectFile.FullName);

        // The new SDK format adds an implicit PackageReference for Aspire.Hosting.AppHost with
        // IsImplicitlyDefined="true". NuGet's Central Package Management (CPM) rejects any
        // PackageVersion entry that targets an implicitly-defined package (NU1009). If the user
        // was previously managing this package through CPM, we must also remove the now-orphaned
        // PackageVersion entry from Directory.Packages.props to prevent the NU1009 error.
        RemovePackageVersionFromDirectoryPackagesProps(projectFile, "Aspire.Hosting.AppHost");

        await Task.CompletedTask;
    }

    private static void RemoveNodeWithWhitespace(XmlNode node)
    {
        var parent = node.ParentNode;
        if (parent is null)
        {
            return;
        }

        // Remove preceding whitespace (any text/whitespace node type)
        var previousSibling = node.PreviousSibling;
        if (previousSibling is XmlCharacterData charData && string.IsNullOrWhiteSpace(charData.Data))
        {
            parent.RemoveChild(previousSibling);
        }

        parent.RemoveChild(node);
    }

    private static void RemovePackageReference(XmlNode projectNode, string packageId)
    {
        var packageNode = projectNode.SelectSingleNode($"//PackageReference[@Include='{packageId}']");
        if (packageNode?.ParentNode is null)
        {
            return;
        }

        var parentNode = packageNode.ParentNode;

        // Remove the package reference and any preceding whitespace
        RemoveNodeWithWhitespace(packageNode);

        // If ItemGroup is now empty (only whitespace), remove it too
        if (parentNode.Name == "ItemGroup" && IsEmptyOrWhitespace(parentNode))
        {
            RemoveNodeWithWhitespace(parentNode);
        }
    }

    private static bool IsEmptyOrWhitespace(XmlNode node)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            // Check for any type of text/whitespace node (XmlText, XmlWhitespace, XmlSignificantWhitespace)
            if (child is XmlCharacterData charData)
            {
                if (!string.IsNullOrWhiteSpace(charData.Data))
                {
                    return false;
                }
            }
            else
            {
                // Non-text node found (element, comment, etc.)
                return false;
            }
        }
        return true;
    }

    private static void RemovePackageVersionFromDirectoryPackagesProps(FileInfo projectFile, string packageId)
    {
        var cpmInfo = DetectCentralPackageManagement(projectFile);
        if (!cpmInfo.UsesCentralPackageManagement || cpmInfo.DirectoryPackagesPropsFile is null)
        {
            return;
        }

        try
        {
            var propsDocument = new XmlDocument();
            propsDocument.PreserveWhitespace = true;
            propsDocument.Load(cpmInfo.DirectoryPackagesPropsFile.FullName);

            var packageVersionNode = propsDocument.SelectSingleNode($"/Project/ItemGroup/PackageVersion[@Include='{packageId}']");
            if (packageVersionNode?.ParentNode is null)
            {
                return;
            }

            var parentNode = packageVersionNode.ParentNode;

            RemoveNodeWithWhitespace(packageVersionNode);

            if (parentNode.Name == "ItemGroup" && IsEmptyOrWhitespace(parentNode))
            {
                RemoveNodeWithWhitespace(parentNode);
            }

            propsDocument.Save(cpmInfo.DirectoryPackagesPropsFile.FullName);
        }
        catch (Exception ex)
        {
            // The csproj has already been updated at this point, so we can't roll back.
            // Inform the user what manual step is needed to complete the migration.
            throw new ProjectUpdaterException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The project file was updated successfully, but the PackageVersion entry for '{0}' could not be " +
                    "removed from '{1}': {2}. Please manually remove the <PackageVersion Include=\"{0}\" ... /> " +
                    "entry from this file to avoid NU1009 build errors.",
                    packageId,
                    cpmInfo.DirectoryPackagesPropsFile.FullName,
                    ex.Message),
                ex);
        }
    }

    private static async Task UpdateSdkVersionInSingleFileAppHostAsync(FileInfo projectFile, NuGetPackageCli package)
    {
        var fileContent = await File.ReadAllTextAsync(projectFile.FullName);

        // Look for the #:sdk Aspire.AppHost.Sdk@<version> directive
        var match = SdkDirectiveRegex().Match(fileContent);

        if (!match.Success)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                "Could not find '#:sdk Aspire.AppHost.Sdk@<version>' directive in single-file AppHost: {0}", projectFile.FullName));
        }

        // Replace the matched SDK directive with the new version
        var newDirective = $"#:sdk Aspire.AppHost.Sdk@{package.Version}";
        var updatedContent = SdkDirectiveRegex().Replace(fileContent, newDirective, 1);

        await File.WriteAllTextAsync(projectFile.FullName, updatedContent);
    }

    [GeneratedRegex(@"#:sdk\s+Aspire\.AppHost\.Sdk@(?:[\d\.\-a-zA-Z]+|\*)")]
    internal static partial Regex SdkDirectiveRegex();

    private async Task AnalyzeProjectAsync(FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        if (!context.VisitedProjects.Add(projectFile.FullName))
        {
            // Project already analyzed, skip
            return;
        }

        // Use fallback wrapper for AppHost project, normal method for others
        var itemsAndPropertiesDocument = IsAppHostProject(projectFile, context)
            ? await GetItemsAndPropertiesWithFallbackAsync(projectFile, context, cancellationToken)
            : await GetItemsAndPropertiesAsync(projectFile, cancellationToken);

        // Check if this project has ManagePackageVersionsCentrally set to false
        var usesCentralPackageManagement = true;
        if (itemsAndPropertiesDocument.RootElement.TryGetProperty("Properties", out var propertiesElement))
        {
            if (propertiesElement.TryGetProperty("ManagePackageVersionsCentrally", out var managePkgVersionsElement))
            {
                var managePkgVersionsValue = managePkgVersionsElement.GetString();
                // If the property is explicitly set to false, don't use CPM even if Directory.Packages.props exists
                if (string.Equals(managePkgVersionsValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    usesCentralPackageManagement = false;
                }
            }
        }

        // Detect if this project uses Central Package Management (if not already disabled by property)
        var cpmInfo = usesCentralPackageManagement
            ? DetectCentralPackageManagement(projectFile)
            : new CentralPackageManagementInfo(false, null);

        var itemsElement = itemsAndPropertiesDocument.RootElement.GetProperty("Items");

        // Handle ProjectReference items (may not exist if project has no project references)
        if (itemsElement.TryGetProperty("ProjectReference", out var projectReferencesElement))
        {
            foreach (var projectReference in projectReferencesElement.EnumerateArray())
            {
                var referencedProjectPath = projectReference.GetProperty("FullPath").GetString() ?? throw new ProjectUpdaterException(UpdateCommandStrings.ProjectReferenceNoFullPath);
                var referencedProjectFile = new FileInfo(referencedProjectPath);
                context.AnalyzeSteps.Enqueue(new AnalyzeStep(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AnalyzeProjectFormat, referencedProjectFile.FullName), () => AnalyzeProjectAsync(referencedProjectFile, context, cancellationToken)));
            }
        }

        // Handle PackageReference items (may not exist if project has no package references)
        if (itemsElement.TryGetProperty("PackageReference", out var packageReferencesElement))
        {
            foreach (var packageReference in packageReferencesElement.EnumerateArray())
            {
            var packageId = packageReference.GetProperty("Identity").GetString() ?? throw new ProjectUpdaterException(UpdateCommandStrings.PackageReferenceNoIdentity);

            if (!IsUpdatablePackage(packageId))
            {
                continue;
            }

            if (cpmInfo.UsesCentralPackageManagement)
            {
                await AnalyzePackageForCentralPackageManagementAsync(packageId, projectFile, cpmInfo.DirectoryPackagesPropsFile!, context, cancellationToken);
            }
            else
            {
                // Traditional package management - Version should be in PackageReference
                if (!packageReference.TryGetProperty("Version", out var versionElement))
                {
                    // Version attribute is missing - treat as wildcard
                    var packageVersion = "*";
                    await AnalyzePackageForTraditionalManagementAsync(packageId, packageVersion, projectFile, context, cancellationToken);
                }
                else
                {
                    var packageVersion = versionElement.GetString();
                    if (string.IsNullOrEmpty(packageVersion) || packageVersion == "*")
                    {
                        // Version is * or empty - treat as wildcard
                        packageVersion = "*";
                    }
                    await AnalyzePackageForTraditionalManagementAsync(packageId, packageVersion, projectFile, context, cancellationToken);
                }
            }
        }
    }
    }

    private static bool IsUpdatablePackage(string packageId)
    {
        // Skip Aspire.Hosting.AppHost - it's removed during SDK update (no longer needed with new SDK format)
        if (string.Equals(packageId, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return packageId.StartsWith("Aspire.", StringComparison.Ordinal);
    }

    private static CentralPackageManagementInfo DetectCentralPackageManagement(FileInfo projectFile)
    {
        // Look for Directory.Packages.props in directory tree.
        for (var current = projectFile.Directory; current is not null; current = current.Parent)
        {
            var directoryPackagesPropsPath = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(directoryPackagesPropsPath))
            {
                return new CentralPackageManagementInfo(true, new FileInfo(directoryPackagesPropsPath));
            }
        }

        return new CentralPackageManagementInfo(false, null);
    }

    private async Task AnalyzePackageForTraditionalManagementAsync(string packageId, string packageVersion, FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        var latestPackage = await GetLatestVersionOfPackageAsync(context, packageId, cancellationToken);

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (IsValidSemanticVersion(packageVersion) && packageVersion == latestPackage?.Version)
        {
            logger.LogInformation("Package '{PackageId}' is up to date.", packageId);
            return;
        }

        var updateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, packageId, packageVersion, latestPackage!.Version),
            () => UpdatePackageReferenceInProject(projectFile, latestPackage, cancellationToken),
            packageId,
            packageVersion,
            latestPackage!.Version,
            projectFile);
        context.UpdateSteps.Enqueue(updateStep);
    }

    private async Task AnalyzePackageForCentralPackageManagementAsync(string packageId, FileInfo projectFile, FileInfo directoryPackagesPropsFile, UpdateContext context, CancellationToken cancellationToken)
    {
        var currentVersion = await GetPackageVersionFromDirectoryPackagesPropsAsync(packageId, directoryPackagesPropsFile, projectFile, cancellationToken);

        if (currentVersion is null)
        {
            logger.LogInformation("Package '{PackageId}' not found in Directory.Packages.props, skipping.", packageId);
            return;
        }

        var latestPackage = await GetLatestVersionOfPackageAsync(context, packageId, cancellationToken);

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (IsValidSemanticVersion(currentVersion) && currentVersion == latestPackage?.Version)
        {
            logger.LogInformation("Package '{PackageId}' is up to date.", packageId);
            return;
        }

        var updateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, packageId, currentVersion, latestPackage!.Version),
            () => UpdatePackageVersionInDirectoryPackagesProps(packageId, latestPackage!.Version, directoryPackagesPropsFile),
            packageId,
            currentVersion,
            latestPackage!.Version,
            projectFile);
        context.UpdateSteps.Enqueue(updateStep);
    }

    private async Task<string?> GetPackageVersionFromDirectoryPackagesPropsAsync(string packageId, FileInfo directoryPackagesPropsFile, FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(directoryPackagesPropsFile.FullName);
            var packageVersionNode = doc.SelectSingleNode($"/Project/ItemGroup/PackageVersion[@Include='{packageId}']");
            var versionAttribute = packageVersionNode?.Attributes?["Version"]?.Value;

            if (versionAttribute is null)
            {
                return null;
            }

            // Check if this is an MSBuild property expression like $(AspireVersion)
            if (IsMSBuildPropertyExpression(versionAttribute))
            {
                var propertyName = ExtractPropertyNameFromExpression(versionAttribute);
                if (propertyName is not null)
                {
                    var resolvedValue = await ResolveMSBuildPropertyAsync(propertyName, projectFile, cancellationToken);
                    if (resolvedValue is not null && IsValidSemanticVersion(resolvedValue))
                    {
                        return resolvedValue;
                    }
                    else
                    {
                        throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                            "Unable to resolve MSBuild property '{0}' to a valid semantic version. Expression: '{1}', Resolved value: '{2}'",
                            propertyName, versionAttribute, resolvedValue ?? "null"));
                    }
                }
                else
                {
                    throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                        "Invalid MSBuild property expression in package version: '{0}'", versionAttribute));
                }
            }

            return versionAttribute;
        }
        catch (ProjectUpdaterException)
        {
            // Re-throw our custom exceptions
            throw;
        }
        catch (Exception ex)
        {
            // Ignore parse errors.
            logger.LogInformation(ex, "Ignoring parsing error in Directory.Packages.props '{DirectoryPackagesPropsFile}' for project '{ProjectFile}'", directoryPackagesPropsFile.FullName, projectFile.FullName);
            return null;
        }
    }

    private static bool IsMSBuildPropertyExpression(string value)
    {
        return value.StartsWith("$(") && value.EndsWith(")") && value.Length > 3;
    }

    private static string? ExtractPropertyNameFromExpression(string expression)
    {
        if (!IsMSBuildPropertyExpression(expression))
        {
            return null;
        }

        // Extract property name from $(PropertyName)
        return expression.Substring(2, expression.Length - 3);
    }

    private async Task<string?> ResolveMSBuildPropertyAsync(string propertyName, FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            var document = await GetItemsAndPropertiesAsync(
                projectFile,
                Array.Empty<string>(), // No items needed
                [propertyName], // Just the property we want
                cancellationToken);

            var propertiesElement = document.RootElement.GetProperty("Properties");
            if (propertiesElement.TryGetProperty(propertyName, out var propertyElement))
            {
                return propertyElement.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while resolving MSBuild property '{PropertyName}' for project '{ProjectFile}'", propertyName, projectFile.FullName);
            return null;
        }
    }

    private static bool IsValidSemanticVersion(string version)
    {
        return SemVersion.TryParse(version, SemVersionStyles.Strict, out _);
    }

    private static async Task UpdatePackageVersionInDirectoryPackagesProps(string packageId, string newVersion, FileInfo directoryPackagesPropsFile)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(directoryPackagesPropsFile.FullName);

        var packageVersionNode = doc.SelectSingleNode($"/Project/ItemGroup/PackageVersion[@Include='{packageId}']");
        if (packageVersionNode?.Attributes?["Version"] is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindPackageVersionInDirectoryPackagesProps, packageId, directoryPackagesPropsFile.FullName));
        }

        packageVersionNode.Attributes["Version"]!.Value = newVersion;
        doc.Save(directoryPackagesPropsFile.FullName);

        await Task.CompletedTask;
    }

    private async Task UpdatePackageReferenceInProject(FileInfo projectFile, NuGetPackageCli package, CancellationToken cancellationToken)
    {
        var exitCode = await runner.AddPackageAsync(
            projectFilePath: projectFile,
            packageName: package.Id,
            packageVersion: package.Version,
            nugetSource: null,
            noRestore: false,
            options: new(),
            cancellationToken: cancellationToken);

        if (exitCode != 0)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.FailedUpdatePackageReferenceFormat, package.Id, projectFile.FullName));
        }
    }

    private async Task<bool> AnalyzeAndConfirmNuGetConfigChanges(FileInfo targetFile, XmlDocument? originalDocument, XmlDocument proposedDocument, CancellationToken cancellationToken)
    {
        interactionService.DisplayEmptyLine();

        var changes = AnalyzeNuGetConfigChanges(originalDocument, proposedDocument);

        if (!changes.HasChanges)
        {
            interactionService.DisplayPlainText(UpdateCommandStrings.NoChangesDetectedInNuGetConfig);
            return true;
        }

        DisplayNuGetConfigChanges(changes);

        var shouldProceed = await interactionService.ConfirmAsync(
            UpdateCommandStrings.ApplyChangesToNuGetConfig,
            defaultValue: true,
            cancellationToken);

        return shouldProceed;
    }

    private static NuGetConfigChanges AnalyzeNuGetConfigChanges(XmlDocument? originalDocument, XmlDocument proposedDocument)
    {
        var changes = new NuGetConfigChanges();

        // Extract package sources from both documents
        var originalSources = ExtractPackageSources(originalDocument);
        var proposedSources = ExtractPackageSources(proposedDocument);

        // Analyze feed changes
        changes.AddedFeeds = proposedSources.Where(p => !originalSources.Any(o => o.Key == p.Key)).ToList();
        changes.RemovedFeeds = originalSources.Where(o => !proposedSources.Any(p => p.Key == o.Key)).ToList();
        changes.RetainedFeeds = originalSources.Where(o => proposedSources.Any(p => p.Key == o.Key)).ToList();

        // Extract package source mappings from both documents
        var originalMappings = ExtractPackageSourceMappings(originalDocument);
        var proposedMappings = ExtractPackageSourceMappings(proposedDocument);

        // Store mappings for display
        changes.OriginalMappings = originalMappings;
        changes.ProposedMappings = proposedMappings;

        // Analyze mapping changes
        changes.MappingChanges = AnalyzeMappingChanges(originalMappings, proposedMappings);

        return changes;
    }

    private static List<PackageSourceInfo> ExtractPackageSources(XmlDocument? document)
    {
        var sources = new List<PackageSourceInfo>();
        if (document?.DocumentElement == null)
        {
            return sources;
        }

        var packageSources = document.DocumentElement.SelectSingleNode("packageSources");
        if (packageSources != null)
        {
            var addNodes = packageSources.SelectNodes("add");
            if (addNodes != null)
            {
                foreach (XmlNode addNode in addNodes)
                {
                    var key = addNode.Attributes?["key"]?.Value;
                    var value = addNode.Attributes?["value"]?.Value;
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        sources.Add(new PackageSourceInfo(key, value));
                    }
                }
            }
        }

        return sources;
    }

    private static Dictionary<string, List<string>> ExtractPackageSourceMappings(XmlDocument? document)
    {
        var mappings = new Dictionary<string, List<string>>();
        if (document?.DocumentElement == null)
        {
            return mappings;
        }

        var packageSourceMapping = document.DocumentElement.SelectSingleNode("packageSourceMapping");
        if (packageSourceMapping != null)
        {
            var packageSourceNodes = packageSourceMapping.SelectNodes("packageSource");
            if (packageSourceNodes != null)
            {
                foreach (XmlNode packageSourceNode in packageSourceNodes)
                {
                    var sourceKey = packageSourceNode.Attributes?["key"]?.Value;
                    if (!string.IsNullOrEmpty(sourceKey))
                    {
                        var patterns = new List<string>();
                        var packageNodes = packageSourceNode.SelectNodes("package");
                        if (packageNodes != null)
                        {
                            foreach (XmlNode packageNode in packageNodes)
                            {
                                var pattern = packageNode.Attributes?["pattern"]?.Value;
                                if (!string.IsNullOrEmpty(pattern))
                                {
                                    patterns.Add(pattern);
                                }
                            }
                        }
                        mappings[sourceKey] = patterns;
                    }
                }
            }
        }

        return mappings;
    }

    private static List<MappingChange> AnalyzeMappingChanges(Dictionary<string, List<string>> originalMappings, Dictionary<string, List<string>> proposedMappings)
    {
        var changes = new List<MappingChange>();

        // Find sources with mapping changes
        var allSources = originalMappings.Keys.Union(proposedMappings.Keys).ToHashSet();

        foreach (var source in allSources)
        {
            var originalPatterns = originalMappings.GetValueOrDefault(source, []);
            var proposedPatterns = proposedMappings.GetValueOrDefault(source, []);

            var addedPatterns = proposedPatterns.Except(originalPatterns).ToList();
            var removedPatterns = originalPatterns.Except(proposedPatterns).ToList();

            if (addedPatterns.Count > 0 || removedPatterns.Count > 0)
            {
                changes.Add(new MappingChange(source, addedPatterns, removedPatterns));
            }
        }

        return changes;
    }

    private void DisplayNuGetConfigChanges(NuGetConfigChanges changes)
    {
        // Create a lookup of mapping changes by source for quick access
        var mappingChangesBySource = changes.MappingChanges.ToDictionary(mc => mc.SourceKey, mc => mc);

        // Display added feeds with their mappings
        foreach (var feed in changes.AddedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AddedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();

            if (changes.ProposedMappings.TryGetValue(feed.Key, out var patterns))
            {
                foreach (var pattern in patterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingAddedFormat, pattern));
                }
            }
            interactionService.DisplayEmptyLine();
        }

        // Display removed feeds
        foreach (var feed in changes.RemovedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.RemovedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();
        }

        // Display retained feeds with their mapping changes and current mappings
        foreach (var feed in changes.RetainedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.RetainedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();

            if (mappingChangesBySource.TryGetValue(feed.Key, out var mappingChange))
            {
                // Show added patterns
                foreach (var pattern in mappingChange.AddedPatterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingAddedFormat, pattern));
                }

                // Show removed patterns
                foreach (var pattern in mappingChange.RemovedPatterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingRemovedFormat, pattern));
                }
            }

            // Show current/unchanged mappings in the proposed configuration
            if (changes.ProposedMappings.TryGetValue(feed.Key, out var currentPatterns))
            {
                var addedPatterns = mappingChangesBySource.TryGetValue(feed.Key, out var currentMappingChange) ? currentMappingChange.AddedPatterns : new List<string>();

                foreach (var pattern in currentPatterns)
                {
                    // Only show patterns that weren't added (they are existing/unchanged)
                    if (!addedPatterns.Contains(pattern))
                    {
                        interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingRetainedFormat, pattern));
                    }
                }
            }
            interactionService.DisplayEmptyLine();
        }
    }
}

internal record PackageSourceInfo(string Key, string Value);

internal record MappingChange(string SourceKey, List<string> AddedPatterns, List<string> RemovedPatterns);

internal class NuGetConfigChanges
{
    public List<PackageSourceInfo> AddedFeeds { get; set; } = [];
    public List<PackageSourceInfo> RemovedFeeds { get; set; } = [];
    public List<PackageSourceInfo> RetainedFeeds { get; set; } = [];
    public List<MappingChange> MappingChanges { get; set; } = [];
    public Dictionary<string, List<string>> OriginalMappings { get; set; } = new();
    public Dictionary<string, List<string>> ProposedMappings { get; set; } = new();

    public bool HasChanges => AddedFeeds.Count > 0 || RemovedFeeds.Count > 0 || MappingChanges.Count > 0;
}

internal sealed class ProjectUpdateResult
{
    public bool UpdatedApplied { get; set; }
}

internal sealed class UpdateContext(FileInfo appHostProjectFile, PackageChannel channel)
{
    public FileInfo AppHostProjectFile { get; } = appHostProjectFile;
    public PackageChannel Channel { get; } = channel;
    public ConcurrentQueue<UpdateStep> UpdateSteps { get; } = new();
    public ConcurrentQueue<AnalyzeStep> AnalyzeSteps { get; } = new();
    public HashSet<string> VisitedProjects { get; } = new();
    public bool FallbackParsing { get; set; }
}

internal abstract record UpdateStep(string Description, Func<Task> Callback)
{
    /// <summary>
    /// Gets the formatted display text using Spectre Console markup for enhanced visual presentation.
    /// </summary>
    public virtual string GetFormattedDisplayText() => Description;
}

/// <summary>
/// Represents an update step for a package reference, containing package and project information.
/// </summary>
internal record PackageUpdateStep(
    string Description,
    Func<Task> Callback,
    string PackageId,
    string CurrentVersion,
    string NewVersion,
    FileInfo ProjectFile) : UpdateStep(Description, Callback)
{
    public override string GetFormattedDisplayText()
    {
        return $"[bold yellow]{PackageId.EscapeMarkup()}[/] [bold green]{CurrentVersion.EscapeMarkup()}[/] to [bold green]{NewVersion.EscapeMarkup()}[/]";
    }
}

internal record AnalyzeStep(string Description, Func<Task> Callback);

internal sealed class ProjectUpdaterException : System.Exception
{
    public ProjectUpdaterException(string message) : base(message) { }
    public ProjectUpdaterException(string message, System.Exception inner) : base(message, inner) { }
}

internal record CentralPackageManagementInfo(bool UsesCentralPackageManagement, FileInfo? DirectoryPackagesPropsFile);

internal record SdkMigrationInfo(bool WillMigrateToNewFormat, bool WillRemoveAppHostPackage);
