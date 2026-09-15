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

    /// <summary>
    /// Resolves the AppHost identity without initializing user secrets or selecting an environment.
    /// </summary>
    public async Task<string> ResolveAppHostIdAsync(
        FileInfo appHostFile,
        IAppHostProject project,
        CancellationToken cancellationToken)
    {
        var userSecretsId = await project.GetUserSecretsIdAsync(appHostFile, autoInit: false, cancellationToken);
        return ResolveAppHostId(appHostFile, userSecretsId);
    }

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

        if (Directory.Exists(secretsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(secretsDirectory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                var environment = Path.GetFileNameWithoutExtension(file);
                results.Add(new AspireSecretsStoreResult(
                    appHostFile,
                    environment,
                    file,
                    new SecretsStore(file),
                    aspireStoreExists: true,
                    legacyUserSecretsFilePath: IsDevelopment(environment) ? legacyUserSecretsFilePath : null));
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

    private string ResolveAppHostId(FileInfo appHostFile, string? userSecretsId)
    {
        var syntheticId = AspireSecretsPathHelper.ComputeSyntheticAppHostId(appHostFile.FullName);
        if (string.IsNullOrWhiteSpace(userSecretsId))
        {
            return syntheticId;
        }

        var sourceDirectory = GetSecretsDirectoryPath(executionContext.HomeDirectory, syntheticId);
        var destinationDirectory = GetSecretsDirectoryPath(executionContext.HomeDirectory, userSecretsId);
        if (string.Equals(sourceDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sourceDirectory))
        {
            return userSecretsId;
        }

        // Deployment prompts and non-Development writes can persist secrets before user-secrets
        // initialization. Move every environment to the real ID, including on read/list paths
        // after an external initialization, without initializing or editing the project here.
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var sourceStore = new SecretsStore(sourceFile);
            var destinationStore = new SecretsStore(Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)));
            foreach (var (key, value) in sourceStore.AsEnumerable())
            {
                if (!destinationStore.ContainsKey(key))
                {
                    destinationStore.Set(key, value);
                }
            }

            // Save atomically before removing the source. A failure must abort resolution so
            // callers cannot delete values while a stale source still awaits migration.
            // Retiring each source separately makes retries safe after a partial migration
            // and prevents later resolutions from resurrecting deleted values.
            destinationStore.Save();
            File.Delete(sourceFile);
        }

        return userSecretsId;
    }

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
