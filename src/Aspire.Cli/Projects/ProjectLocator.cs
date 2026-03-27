// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal interface IProjectLocator
{
    Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default);
    Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the AppHost project file from <c>.aspire/settings.json</c> only, without any
    /// user interaction or recursive filesystem scanning. Returns <c>null</c> when no settings
    /// file or <c>appHostPath</c> entry is found.
    /// </summary>
    Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default);
}

internal sealed class ProjectLocator(
    ILogger<ProjectLocator> logger,
    CliExecutionContext executionContext,
    IInteractionService interactionService,
    IConfigurationService configurationService,
    IAppHostProjectFactory projectFactory,
    ILanguageDiscovery languageDiscovery,
    IDotNetSdkInstaller sdkInstaller,
    AspireCliTelemetry telemetry) : IProjectLocator
{

    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(string searchDirectory, CancellationToken cancellationToken)
    {
        var allCandidates = await FindAppHostProjectFilesAsync(new DirectoryInfo(searchDirectory), cancellationToken);
        return [..allCandidates.BuildableAppHost, ..allCandidates.UnbuildableSuspectedAppHostProjects];
    }

    private async Task<(List<FileInfo> BuildableAppHost, List<FileInfo> UnbuildableSuspectedAppHostProjects, bool HasUnsupportedProjects)> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        return await interactionService.ShowStatusAsync(InteractionServiceStrings.SearchingProjects, async () =>
        {
            var appHostProjects = new List<FileInfo>();
            var unbuildableSuspectedAppHostProjects = new List<FileInfo>();
            var hasUnsupportedProjects = false;
            var lockObject = new object();
            logger.LogDebug("Searching for project files in {SearchDirectory}", searchDirectory.FullName);
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            interactionService.DisplayMessage(KnownEmojis.MagnifyingGlassTiltedLeft, InteractionServiceStrings.FindingAppHosts);

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            // Get detection patterns from all languages
            var allLanguages = await languageDiscovery.GetAvailableLanguagesAsync(cancellationToken);
            var allPatterns = allLanguages.SelectMany(l => l.DetectionPatterns).Distinct().ToArray();

            logger.LogDebug("Searching for patterns: {Patterns}", string.Join(", ", allPatterns));

            var nugetCachePath = GetNuGetPackagesCachePath();
            logger.LogDebug("NuGet cache path to exclude: {NuGetCachePath}", nugetCachePath ?? "(none)");

            // Collect all candidates with their handlers across all patterns
            var candidatesWithHandlers = new List<(FileInfo File, IAppHostProject Handler)>();

            foreach (var pattern in allPatterns)
            {
                var candidateFiles = FindMatchingFiles(searchDirectory, pattern, enumerationOptions, nugetCachePath);
                logger.LogDebug("Found {CandidateCount} files matching pattern '{Pattern}'", candidateFiles.Length, pattern);

                foreach (var candidateFile in candidateFiles)
                {
                    logger.LogDebug("Checking candidate file {CandidateFile}", candidateFile.FullName);

                    var handler = projectFactory.TryGetProject(candidateFile);
                    if (handler is null)
                    {
                        logger.LogTrace("No handler found for {CandidateFile}", candidateFile.FullName);
                        continue;
                    }

                    candidatesWithHandlers.Add((candidateFile, handler));
                }
            }

            // If any candidates are .NET projects, ensure the SDK is available
            var dotNetCandidate = candidatesWithHandlers.FirstOrDefault(c => c.Handler.LanguageId.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase));
            if (dotNetCandidate.Handler is { } dotNetHandler)
            {
                // TODO: Consider moving this check inside the handler.
                // Would need to support caching and reusing check across validations.
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(sdkInstaller, interactionService, telemetry, cancellationToken))
                {
                    logger.LogWarning("The .NET SDK is not available. Marking .NET projects as unsupported.");
                    dotNetHandler.IsUnsupported = true;
                }
            }

            await Parallel.ForEachAsync(candidatesWithHandlers, parallelOptions, async (candidate, ct) =>
            {
                var (candidateFile, handler) = candidate;

                // Validate the candidate file using the handler
                var validationResult = await handler.ValidateAppHostAsync(candidateFile, ct);

                if (validationResult.IsValid)
                {
                    logger.LogDebug("Found {Language} apphost {CandidateFile}", handler.DisplayName, candidateFile.FullName);
                    var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                    interactionService.DisplaySubtleMessage(relativePath);
                    lock (lockObject)
                    {
                        appHostProjects.Add(candidateFile);
                    }
                }
                else if (validationResult.IsUnsupported)
                {
                    var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileUnsupportedInCurrentEnvironment, relativePath));
                    logger.LogDebug("Skipping unsupported project {CandidateFile}", candidateFile.FullName);
                    hasUnsupportedProjects = true;
                }
                else if (validationResult.IsPossiblyUnbuildable)
                {
                    var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileMayBeUnbuildableAppHost, relativePath));
                    lock (lockObject)
                    {
                        unbuildableSuspectedAppHostProjects.Add(candidateFile);
                    }
                }
                else
                {
                    logger.LogTrace("File {CandidateFile} is not a valid Aspire host", candidateFile.FullName);
                }
            });

            // This sort is done here to make results deterministic since we get all the app
            // host information in parallel and the order may vary.
            appHostProjects.Sort((x, y) => x.FullName.CompareTo(y.FullName));

            return (appHostProjects, unbuildableSuspectedAppHostProjects, hasUnsupportedProjects);
        });
    }

    /// <inheritdoc />
    public async Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAppHostProjectFileFromSettingsAsync(silent: true, cancellationToken);
    }

    private async Task<FileInfo?> GetAppHostProjectFileFromSettingsAsync(CancellationToken cancellationToken)
    {
        return await GetAppHostProjectFileFromSettingsAsync(silent: false, cancellationToken);
    }

    private async Task<FileInfo?> GetAppHostProjectFileFromSettingsAsync(bool silent, CancellationToken cancellationToken)
    {
        var searchDirectory = executionContext.WorkingDirectory;

        while (true)
        {
            // Check aspire.config.json first
            AspireConfigFile? aspireConfig;
            try
            {
                aspireConfig = AspireConfigFile.Load(searchDirectory.FullName);
            }
            catch (JsonException ex)
            {
                interactionService.DisplayError(ex.Message);
                return null;
            }
            if (aspireConfig?.AppHost?.Path is { } configAppHostPath)
            {
                var qualifiedPath = Path.IsPathRooted(configAppHostPath)
                    ? configAppHostPath
                    : Path.Combine(searchDirectory.FullName, configAppHostPath);
                qualifiedPath = PathNormalizer.NormalizePathForCurrentPlatform(qualifiedPath);
                var appHostFile = new FileInfo(qualifiedPath);

                if (appHostFile.Exists)
                {
                    logger.LogInformation("Found AppHost path '{AppHostPath}' from config file in {Directory}", configAppHostPath, searchDirectory.FullName);
                    return appHostFile;
                }
                else
                {
                    var configFilePath = Path.Combine(searchDirectory.FullName, AspireConfigFile.FileName);
                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostWasSpecifiedButDoesntExist, configFilePath, qualifiedPath));
                    return null;
                }
            }

            // TODO: Remove legacy .aspire/settings.json fallback once confident most users have migrated.
            // Tracked by https://github.com/microsoft/aspire/issues/15239
            // Fall back to .aspire/settings.json
            var settingsFile = new FileInfo(ConfigurationHelper.BuildPathToSettingsJsonFile(searchDirectory.FullName));

            if (settingsFile.Exists)
            {
                using var stream = settingsFile.OpenRead();
                var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (json.RootElement.TryGetProperty("appHostPath", out var appHostPathProperty) && appHostPathProperty.GetString() is { } appHostPath)
                {
                    var qualifiedAppHostPath = Path.IsPathRooted(appHostPath) ? appHostPath : Path.Combine(settingsFile.Directory!.FullName, appHostPath);
                    qualifiedAppHostPath = PathNormalizer.NormalizePathForCurrentPlatform(qualifiedAppHostPath);
                    var appHostFile = new FileInfo(qualifiedAppHostPath);

                    if (appHostFile.Exists)
                    {
                        return appHostFile;
                    }
                    else
                    {
                        // AppHost file was specified but doesn't exist, return null to trigger fallback logic
                        if (!silent)
                        {
                            interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostWasSpecifiedButDoesntExist, settingsFile.FullName, qualifiedAppHostPath));
                        }
                        return null;
                    }
                }
            }

            if (searchDirectory.Parent is not null)
            {
                searchDirectory = searchDirectory.Parent;
            }
            else
            {
                return null;
            }
        }
    }

    public async Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Finding project file in {CurrentDirectory}", executionContext.WorkingDirectory);

        if (projectFile is not null)
        {
            // Check if the provided path is actually a directory
            if (Directory.Exists(projectFile.FullName))
            {
                logger.LogDebug("Provided path {Path} is a directory, searching for project files recursively", projectFile.FullName);
                var directory = new DirectoryInfo(projectFile.FullName);

                // Reuse the main search logic
                var searchResults = await FindAppHostProjectFilesAsync(directory, cancellationToken);
                var appHostProjects = searchResults.BuildableAppHost;

                interactionService.DisplayEmptyLine();

                if (appHostProjects.Count == 0)
                {
                    if (searchResults.HasUnsupportedProjects)
                    {
                        throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.UnsupportedProjects);
                    }

                    logger.LogError("No AppHost project files found in directory {Directory}", directory.FullName);
                    throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
                }
                else if (appHostProjects.Count == 1)
                {
                    logger.LogDebug("Found single AppHost project file {ProjectFile} in directory {Directory}", appHostProjects[0].FullName, directory.FullName);
                    projectFile = appHostProjects[0];
                }
                else
                {
                    if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Prompt)
                    {
                        logger.LogDebug("Multiple AppHost project files found in directory {Directory}, prompting user to select", directory.FullName);
                        projectFile = await interactionService.PromptForSelectionAsync(
                            InteractionServiceStrings.SelectAppHostToUse,
                            appHostProjects,
                            file => $"{file.Name.EscapeMarkup()} ({Path.GetRelativePath(executionContext.WorkingDirectory.FullName, file.FullName).EscapeMarkup()})",
                            cancellationToken
                        );
                    }
                    else if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.None)
                    {
                        logger.LogDebug("Multiple AppHost project files found in directory {Directory}, selecting none", directory.FullName);
                        projectFile = null;
                    }
                    else if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Throw)
                    {
                        logger.LogError("Multiple AppHost project files found in directory {Directory}, throwing exception", directory.FullName);
                        throw new ProjectLocatorException(ErrorStrings.MultipleProjectFilesFound, ProjectLocatorFailureReason.MultipleProjectFilesFound);
                    }
                }
            }

            if (projectFile is not null)
            {
                // If the project file is passed, validate it.
                if (!projectFile.Exists)
                {
                    logger.LogError("Project file {ProjectFile} does not exist.", projectFile.FullName);
                    throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
                }

                // Check if any handler can handle this file
                var handler = projectFactory.TryGetProject(projectFile);
                if (handler is not null)
                {
                    // The handler still may have matched an invalid single file apphost, so validate it before accepting as the selected project file
                    var validationResult = await handler.ValidateAppHostAsync(projectFile, cancellationToken);
                    if (validationResult.IsValid)
                    {
                        logger.LogDebug("Using {Language} apphost {ProjectFile}", handler.DisplayName, projectFile.FullName);
                        return new AppHostProjectSearchResult(projectFile, [projectFile]);
                    }
                }

                // If no handler matched, for .cs files check if we should search the parent directory
                if (projectFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase) && projectFile.Directory is { } parentDirectory)
                {
                    // File exists but is not a valid single-file apphost. Search in the parent directory
                    return await UseOrFindAppHostProjectFileAsync(new FileInfo(parentDirectory.FullName), multipleAppHostProjectsFoundBehavior, createSettingsFile, cancellationToken);
                }

                // No handler can process this file
                throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
            }
        }

        projectFile = await GetAppHostProjectFileFromSettingsAsync(cancellationToken);

        if (projectFile is not null)
        {
            if (createSettingsFile)
            {
                await CreateSettingsFileAsync(projectFile, cancellationToken);
            }

            return new AppHostProjectSearchResult(projectFile, [projectFile]);
        }

        logger.LogDebug("No project file specified, searching for apphost projects in {CurrentDirectory}", executionContext.WorkingDirectory);
        var results = await FindAppHostProjectFilesAsync(executionContext.WorkingDirectory, cancellationToken);

        logger.LogDebug("Found {ProjectFileCount} project files.", results.BuildableAppHost.Count);

        FileInfo? selectedAppHost = null;

        if (results.BuildableAppHost.Count == 0 && results.UnbuildableSuspectedAppHostProjects.Count == 0)
        {
            if (results.HasUnsupportedProjects)
            {
                throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.UnsupportedProjects);
            }

            throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound);
        }
        else if (results.BuildableAppHost.Count == 0 && results.UnbuildableSuspectedAppHostProjects.Count > 0)
        {
            throw new ProjectLocatorException(ErrorStrings.AppHostsMayNotBeBuildable, ProjectLocatorFailureReason.AppHostsMayNotBeBuildable);
        }
        else if (results.BuildableAppHost.Count == 1)
        {
            selectedAppHost = results.BuildableAppHost[0];
        }
        else if (results.BuildableAppHost.Count > 1)
        {
            selectedAppHost = multipleAppHostProjectsFoundBehavior switch
            {
                MultipleAppHostProjectsFoundBehavior.Throw => throw new ProjectLocatorException(ErrorStrings.MultipleProjectFilesFound, ProjectLocatorFailureReason.MultipleProjectFilesFound),
                MultipleAppHostProjectsFoundBehavior.Prompt => await interactionService.PromptForSelectionAsync(InteractionServiceStrings.SelectAppHostToUse, results.BuildableAppHost, projectFile => $"{projectFile.Name.EscapeMarkup()} ({Path.GetRelativePath(executionContext.WorkingDirectory.FullName, projectFile.FullName).EscapeMarkup()})", cancellationToken),
                MultipleAppHostProjectsFoundBehavior.None => null,
                _ => selectedAppHost
            };
        }

        if (createSettingsFile)
        {
            await CreateSettingsFileAsync(selectedAppHost!, cancellationToken);
        }

        return new AppHostProjectSearchResult(selectedAppHost, results.BuildableAppHost);
    }

    public async Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        var result = await UseOrFindAppHostProjectFileAsync(projectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile, cancellationToken);
        return result.SelectedProjectFile;
    }

    private async Task CreateSettingsFileAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        var settingsFile = GetOrCreateLocalAspireConfigFile();
        var fileExisted = settingsFile.Exists;

        logger.LogDebug("Creating settings file at {SettingsFilePath}", settingsFile.FullName);

        var relativePathToProjectFile = Path.GetRelativePath(settingsFile.Directory!.FullName, projectFile.FullName).Replace(Path.DirectorySeparatorChar, '/');

        // Use the configuration writer to set the AppHost path, which will merge with any existing settings.
        await configurationService.SetConfigurationAsync("appHost.path", relativePathToProjectFile, isGlobal: false, cancellationToken);

        // For polyglot projects, also set language and inherit SDK version from parent/global config.
        var language = languageDiscovery.GetLanguageByFile(projectFile);
        if (language is not null && !language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            await configurationService.SetConfigurationAsync("appHost.language", language.LanguageId.Value, isGlobal: false, cancellationToken);

            // Inherit SDK version from parent/global config if available.
            var inheritedSdkVersion = await configurationService.GetConfigurationAsync("sdk.version", cancellationToken)
                ?? await configurationService.GetConfigurationAsync("sdkVersion", cancellationToken);
            if (!string.IsNullOrEmpty(inheritedSdkVersion))
            {
                await configurationService.SetConfigurationAsync("sdk.version", inheritedSdkVersion, isGlobal: false, cancellationToken);
                logger.LogDebug("Set SDK version {Version} in settings file (inherited from parent config)", inheritedSdkVersion);
            }
        }

        var relativeSettingsFilePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, settingsFile.FullName).Replace(Path.DirectorySeparatorChar, '/');
        var message = fileExisted ? InteractionServiceStrings.UpdatedSettingsFile : InteractionServiceStrings.CreatedSettingsFile;
        interactionService.DisplayMessage(KnownEmojis.FileCabinet, string.Format(CultureInfo.CurrentCulture, message, $"[bold]'{relativeSettingsFilePath.EscapeMarkup()}'[/]"), allowMarkup: true);
    }

    private FileInfo GetOrCreateLocalAspireConfigFile()
    {
        var settingsFile = new FileInfo(configurationService.GetSettingsFilePath(isGlobal: false));

        if (string.Equals(settingsFile.Name, AspireConfigFile.FileName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Using existing config file at {Path}", settingsFile.FullName);
            return settingsFile;
        }

        var legacySettingsRootDirectory = GetLegacySettingsRootDirectory(settingsFile);
        if (legacySettingsRootDirectory is null)
        {
            var newConfigPath = Path.Combine(executionContext.WorkingDirectory.FullName, AspireConfigFile.FileName);
            logger.LogDebug("No existing config found, will create new config at {Path}", newConfigPath);
            return new FileInfo(newConfigPath);
        }

        var aspireConfigFile = new FileInfo(Path.Combine(legacySettingsRootDirectory.FullName, AspireConfigFile.FileName));
        if (!aspireConfigFile.Exists)
        {
            logger.LogInformation("Migrating legacy settings from {LegacyDir} to {ConfigFile}", legacySettingsRootDirectory.FullName, aspireConfigFile.FullName);
            MigrateLegacySettings(legacySettingsRootDirectory);
        }

        return aspireConfigFile;
    }

    private void MigrateLegacySettings(DirectoryInfo settingsRootDirectory)
    {
        var configFilePath = Path.Combine(settingsRootDirectory.FullName, AspireConfigFile.FileName);
        logger.LogInformation("Migrating legacy settings to {SettingsFilePath}", configFilePath);

        // LoadOrCreate handles the legacy fallback and migration internally,
        // including saving the migrated config to disk.
        _ = AspireConfigFile.LoadOrCreate(settingsRootDirectory.FullName);
    }

    private static DirectoryInfo? GetLegacySettingsRootDirectory(FileInfo settingsFile)
    {
        if (!string.Equals(settingsFile.Name, AspireJsonConfiguration.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var settingsDirectory = settingsFile.Directory;
        if (settingsDirectory is null || !string.Equals(settingsDirectory.Name, AspireJsonConfiguration.SettingsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return settingsDirectory.Parent;
    }

    private static FileInfo[] FindMatchingFiles(DirectoryInfo searchDirectory, string pattern, EnumerationOptions options, string? excludePath)
    {
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var enumerable = new FileSystemEnumerable<FileInfo>(
            searchDirectory.FullName,
            (ref FileSystemEntry entry) => new FileInfo(entry.ToFullPath()),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, entry.FileName),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                if (excludePath is null)
                {
                    return true;
                }
                var dirPath = entry.ToFullPath();
                return !dirPath.Equals(excludePath, pathComparison)
                    && !dirPath.StartsWith(excludePath + Path.DirectorySeparatorChar, pathComparison);
            }
        };

        return enumerable.ToArray();
    }

    private string? GetNuGetPackagesCachePath()
    {
        var envPath = executionContext.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var userProfile = executionContext.HomeDirectory.FullName;
        if (!string.IsNullOrEmpty(userProfile))
        {
            return Path.GetFullPath(Path.Combine(userProfile, ".nuget", "packages"));
        }

        return null;
    }

}

internal class ProjectLocatorException(string message, ProjectLocatorFailureReason failureReason) : System.Exception(message)
{
    public ProjectLocatorFailureReason FailureReason { get; } = failureReason;
}

internal enum ProjectLocatorFailureReason
{
    ProjectFileDoesntExist,
    ProjectFileNotAppHostProject,
    MultipleProjectFilesFound,
    NoProjectFileFound,
    AppHostsMayNotBeBuildable,
    UnsupportedProjects,
}

internal record AppHostProjectSearchResult(FileInfo? SelectedProjectFile, List<FileInfo> AllProjectFileCandidates);

internal enum MultipleAppHostProjectsFoundBehavior
{
    Prompt,
    Throw,
    None
}
