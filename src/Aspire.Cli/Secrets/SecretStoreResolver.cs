// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Shared.UserSecrets;

namespace Aspire.Cli.Secrets;

/// <summary>
/// Resolves the UserSecretsId for an AppHost and creates a SecretsStore.
/// </summary>
internal sealed class SecretStoreResolver(
    IProjectLocator projectLocator,
    IAppHostProjectFactory projectFactory)
{
    /// <summary>
    /// Resolves the secrets store for the given (or auto-discovered) AppHost project.
    /// </summary>
    public async Task<SecretsStoreResult?> ResolveAsync(
        FileInfo? projectFile,
        bool autoInit,
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

        var userSecretsId = await project.GetUserSecretsIdAsync(appHostFile, autoInit, cancellationToken);
        if (string.IsNullOrEmpty(userSecretsId))
        {
            return null;
        }

        var secretsFilePath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        var store = new SecretsStore(secretsFilePath);

        return new SecretsStoreResult(store, userSecretsId, appHostFile);
    }
}

/// <summary>
/// Result of resolving a secrets store for an AppHost.
/// </summary>
internal sealed record SecretsStoreResult(
    SecretsStore Store,
    string UserSecretsId,
    FileInfo AppHostFile);
