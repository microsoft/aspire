// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines.Internal;

/// <summary>
/// File-based deployment state manager for deployment scenarios.
/// </summary>
internal sealed partial class FileDeploymentStateManager(
    ILogger<FileDeploymentStateManager> logger,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    IOptions<PipelineOptions> pipelineOptions) : DeploymentStateManagerBase<FileDeploymentStateManager>(logger)
{
    private readonly JsonObject _migratedState = [];
    private readonly HashSet<string> _migratedSectionNames = new(StringComparer.Ordinal);
    private bool _isMigratingLegacyState;

    // Regex pattern matching only alphanumeric characters, underscores, and hyphens
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidEnvironmentNameRegex();

    /// <inheritdoc/>
    public override string? StateFilePath => GetStatePath();

    /// <summary>
    /// Validates that the environment name contains only allowed characters and is safe for use in file paths.
    /// </summary>
    /// <param name="environmentName">The environment name to validate.</param>
    /// <returns><c>true</c> if the environment name is valid; otherwise, <c>false</c>.</returns>
    internal static bool IsValidEnvironmentName(string environmentName)
    {
        if (string.IsNullOrEmpty(environmentName))
        {
            return false;
        }

        // Validate against allowed characters: [a-zA-Z0-9_-]+
        // This pattern also guards against path traversal attacks since it doesn't allow
        // dots (.), slashes (/), or backslashes (\)
        return ValidEnvironmentNameRegex().IsMatch(environmentName);
    }

    /// <inheritdoc/>
    protected override string? GetStatePath()
    {
        var currentStatePath = GetStatePath(configuration["AppHost:PathSha256"], hostEnvironment.EnvironmentName);
        if (currentStatePath is null || File.Exists(currentStatePath))
        {
            return currentStatePath;
        }

        if (pipelineOptions.Value.ClearCache)
        {
            return currentStatePath;
        }

        var legacyStatePath = GetStatePath(configuration["AppHost:LegacyPathSha256"], hostEnvironment.EnvironmentName);
        if (legacyStatePath is not null && File.Exists(legacyStatePath))
        {
            _isMigratingLegacyState = true;
            return legacyStatePath;
        }

        return currentStatePath;
    }

    private string? GetCanonicalStatePath() => GetStatePath(configuration["AppHost:PathSha256"], hostEnvironment.EnvironmentName);

    private string? GetStatePath(string? appHostSha, string environmentName)
    {
        if (string.IsNullOrEmpty(appHostSha))
        {
            return null;
        }

        var environment = environmentName.ToLowerInvariant();

        // Validate the environment name to ensure it only contains safe characters
        // and guard against path traversal attacks
        if (!IsValidEnvironmentName(environment))
        {
            throw new ArgumentException($"The environment name '{environment}' contains invalid characters. Environment names must only contain alphanumeric characters, underscores, and hyphens ([a-zA-Z0-9_-]+).", nameof(environmentName));
        }

        var aspireHome = configuration[KnownConfigNames.AspireHome];
        if (string.IsNullOrWhiteSpace(aspireHome))
        {
            aspireHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aspire");
        }

        var aspireDir = Path.Combine(
            aspireHome,
            "deployments",
            appHostSha
        );

        return Path.Combine(aspireDir, $"{environment}.json");
    }

    /// <inheritdoc/>
    protected override JsonNode? GetSectionState(JsonObject? state, string sectionName, bool includeLegacyState)
    {
        if (_isMigratingLegacyState &&
            (!includeLegacyState ||
             _migratedSectionNames.Contains(sectionName) ||
             _migratedSectionNames.Any(name => name.StartsWith($"{sectionName}:", StringComparison.Ordinal))))
        {
            return TryGetNestedPropertyValue(_migratedState, sectionName);
        }

        return base.GetSectionState(state, sectionName, includeLegacyState);
    }

    /// <inheritdoc/>
    protected override async Task SaveStateToStorageAsync(JsonObject state, string? sectionName, CancellationToken cancellationToken)
    {
        try
        {
            if (pipelineOptions.Value.ClearCache)
            {
                logger.LogInformation("Skipping deployment state save due to --clear-cache flag");
                return;
            }

            var deploymentStatePath = GetCanonicalStatePath();
            if (deploymentStatePath is null)
            {
                logger.LogWarning("Cannot save deployment state: AppHostSha is not configured");
                return;
            }

            var stateToSave = state;
            if (_isMigratingLegacyState && sectionName is not null)
            {
                // Source/polyglot AppHosts historically shared one directory-scoped state file.
                // Persist only sections this AppHost has actually updated so sibling AppHosts
                // remain available in the legacy file and cannot be mistaken for stale resources.
                var sectionData = TryGetNestedPropertyValue(state, sectionName) as JsonObject;
                SetNestedPropertyValue(_migratedState, sectionName, sectionData?.DeepClone().AsObject());
                _migratedSectionNames.Add(sectionName);
                stateToSave = _migratedState;
            }

            var flattenedSecrets = JsonFlattener.FlattenJsonObject(stateToSave);
            var deploymentStateDirectory = Path.GetDirectoryName(deploymentStatePath)!;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Directory.CreateDirectory(deploymentStateDirectory);
            }
            else
            {
                var expectedMode = UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead;
                // Always call CreateDirectory first to avoid race conditions.
                // CreateDirectory is a no-op if the directory already exists but won't change existing permissions.
                Directory.CreateDirectory(deploymentStateDirectory, expectedMode);

                try
                {
                    var currentMode = File.GetUnixFileMode(deploymentStateDirectory);
                    if ((currentMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    {
                        logger.LogWarning(
                            "Deployment state directory '{Directory}' has permissions that allow access to other users. " +
                            "Consider restricting permissions to the current user only by running: chmod 700 {Directory}",
                            deploymentStateDirectory,
                            deploymentStateDirectory);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Unable to check permissions on deployment state directory '{Directory}'.", deploymentStateDirectory);
                }
            }
            await File.WriteAllTextAsync(
                deploymentStatePath,
                flattenedSecrets.ToJsonString(UserSecretsJsonOptions.s_instance),
                cancellationToken).ConfigureAwait(false);

            logger.LogDebug("Deployment state saved to {Path}", deploymentStatePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save deployment state.");
            throw;
        }
    }
}
