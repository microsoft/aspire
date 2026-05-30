// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Shared.UserSecrets;

namespace Aspire.Cli.Secrets;

/// <summary>
/// Resolves CLI-owned Aspire secrets files for AppHost environments.
/// </summary>
internal sealed class AspireSecretsStoreResolver(
    IProjectLocator projectLocator,
    IAppHostProjectFactory projectFactory,
    CliExecutionContext executionContext)
{
    internal const string DefaultEnvironmentName = AppHostEnvironmentDefaults.DevelopmentEnvironmentName;

    public async Task<AspireSecretsStoreResult?> ResolveAsync(
        FileInfo? projectFile,
        string? environmentName,
        bool autoInitDevelopmentUserSecrets,
        CancellationToken cancellationToken)
    {
        var searchResult = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile,
            MultipleAppHostProjectsFoundBehavior.Prompt,
            createSettingsFile: false,
            cancellationToken);

        var appHostFile = searchResult.SelectedProjectFile;
        if (appHostFile is null)
        {
            return null;
        }

        var project = projectFactory.TryGetProject(appHostFile);
        if (project is null)
        {
            return null;
        }

        return await ResolveAsync(appHostFile, project, environmentName, autoInitDevelopmentUserSecrets, cancellationToken);
    }

    public Task<AspireSecretsStoreResult?> ResolveAsync(
        FileInfo? projectFile,
        string? environmentName,
        CancellationToken cancellationToken)
    {
        return ResolveAsync(projectFile, environmentName, autoInitDevelopmentUserSecrets: false, cancellationToken);
    }

    public async Task<AspireSecretsStoreResult?> ResolveAsync(
        FileInfo appHostFile,
        IAppHostProject project,
        string? environmentName,
        bool autoInitDevelopmentUserSecrets,
        CancellationToken cancellationToken)
    {
        var resolvedEnvironment = ResolveEnvironmentName(environmentName);
        var isDevelopment = IsDevelopment(resolvedEnvironment);
        var existingUserSecretsId = await project.GetUserSecretsIdAsync(
            appHostFile,
            autoInit: autoInitDevelopmentUserSecrets && isDevelopment,
            cancellationToken);

        var appHostId = ResolveAppHostId(appHostFile, existingUserSecretsId);
        var filePath = AspireSecretsPathHelper.GetSecretsFilePath(executionContext.HomeDirectory, appHostId, resolvedEnvironment);
        var legacyUserSecretsFilePath = GetLegacyUserSecretsFilePath(existingUserSecretsId, isDevelopment);
        ImportLegacyUserSecretsIfNeeded(legacyUserSecretsFilePath, filePath);

        return new AspireSecretsStoreResult(
            appHostFile,
            resolvedEnvironment,
            filePath,
            new SecretsStore(filePath),
            File.Exists(filePath),
            legacyUserSecretsFilePath);
    }

    public Task<AspireSecretsStoreResult?> ResolveAsync(
        FileInfo appHostFile,
        IAppHostProject project,
        string? environmentName,
        CancellationToken cancellationToken)
    {
        return ResolveAsync(appHostFile, project, environmentName, autoInitDevelopmentUserSecrets: false, cancellationToken);
    }

    public async Task<IReadOnlyList<AspireSecretsStoreResult>?> ResolveAllExistingAsync(
        FileInfo? projectFile,
        CancellationToken cancellationToken)
    {
        var searchResult = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile,
            MultipleAppHostProjectsFoundBehavior.Prompt,
            createSettingsFile: false,
            cancellationToken);

        var appHostFile = searchResult.SelectedProjectFile;
        if (appHostFile is null)
        {
            return null;
        }

        var project = projectFactory.TryGetProject(appHostFile);
        if (project is null)
        {
            return null;
        }

        return await ResolveAllExistingAsync(appHostFile, project, cancellationToken);
    }

    public async Task<IReadOnlyList<AspireSecretsStoreResult>> ResolveAllExistingAsync(
        FileInfo appHostFile,
        IAppHostProject project,
        CancellationToken cancellationToken)
    {
        var existingUserSecretsId = await project.GetUserSecretsIdAsync(appHostFile, autoInit: false, cancellationToken);
        var appHostId = ResolveAppHostId(appHostFile, existingUserSecretsId);
        var secretsDirectory = AspireSecretsPathHelper.GetSecretsDirectoryPath(executionContext.HomeDirectory, appHostId);
        var results = new List<AspireSecretsStoreResult>();
        var legacyUserSecretsFilePath = GetLegacyUserSecretsFilePath(existingUserSecretsId, isDevelopment: true);
        var developmentFilePath = AspireSecretsPathHelper.GetSecretsFilePath(executionContext.HomeDirectory, appHostId, DefaultEnvironmentName);

        ImportLegacyUserSecretsIfNeeded(legacyUserSecretsFilePath, developmentFilePath);

        if (File.Exists(developmentFilePath))
        {
            results.Add(new AspireSecretsStoreResult(
                appHostFile,
                DefaultEnvironmentName,
                developmentFilePath,
                new SecretsStore(developmentFilePath),
                aspireStoreExists: true,
                legacyUserSecretsFilePath));
        }

        if (Directory.Exists(secretsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(secretsDirectory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                var environment = Path.GetFileNameWithoutExtension(file);
                if (IsDevelopment(environment))
                {
                    continue;
                }

                results.Add(new AspireSecretsStoreResult(
                    appHostFile,
                    environment,
                    file,
                    new SecretsStore(file),
                    aspireStoreExists: true,
                    legacyUserSecretsFilePath: null));
            }
        }

        return results;
    }

    internal static string ResolveEnvironmentName(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName) ? DefaultEnvironmentName : environmentName;

    internal static string GetSecretsFilePath(DirectoryInfo homeDirectory, string appHostId, string environmentName) =>
        AspireSecretsPathHelper.GetSecretsFilePath(homeDirectory, appHostId, environmentName);

    internal static string GetSecretsDirectoryPath(DirectoryInfo homeDirectory, string appHostId) =>
        AspireSecretsPathHelper.GetSecretsDirectoryPath(homeDirectory, appHostId);

    private static bool IsDevelopment(string environmentName) =>
        string.Equals(environmentName, AppHostEnvironmentDefaults.DevelopmentEnvironmentName, StringComparison.OrdinalIgnoreCase);

    private static string ResolveAppHostId(FileInfo appHostFile, string? userSecretsId) =>
        string.IsNullOrWhiteSpace(userSecretsId) ? AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName) : userSecretsId;

    private static string? GetLegacyUserSecretsFilePath(string? userSecretsId, bool isDevelopment) =>
        isDevelopment && !string.IsNullOrWhiteSpace(userSecretsId) ? UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId) : null;

    private static void ImportLegacyUserSecretsIfNeeded(string? legacyUserSecretsFilePath, string aspireSecretsFilePath)
    {
        if (legacyUserSecretsFilePath is null || !File.Exists(legacyUserSecretsFilePath) || File.Exists(aspireSecretsFilePath))
        {
            return;
        }

        var legacyStore = new SecretsStore(legacyUserSecretsFilePath);
        if (legacyStore.Count == 0)
        {
            return;
        }

        var aspireStore = new SecretsStore(aspireSecretsFilePath);
        foreach (var (key, value) in legacyStore.AsEnumerable())
        {
            aspireStore.Set(key, value);
        }

        aspireStore.Save();
    }
}

internal sealed class AspireSecretsStoreResult(
    FileInfo appHostFile,
    string environmentName,
    string aspireSecretsFilePath,
    SecretsStore aspireStore,
    bool aspireStoreExists,
    string? legacyUserSecretsFilePath)
{
    public FileInfo AppHostFile { get; } = appHostFile;
    public string EnvironmentName { get; } = environmentName;
    public string AspireSecretsFilePath { get; } = aspireSecretsFilePath;
    public SecretsStore AspireStore { get; } = aspireStore;
    public bool AspireStoreExists { get; private set; } = aspireStoreExists;
    public string? LegacyUserSecretsFilePath { get; } = legacyUserSecretsFilePath;

    public SecretsStore GetReadStore() =>
        AspireStore;

    public SecretsStore GetDeleteStore(string _) =>
        AspireStore;
}
