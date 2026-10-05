// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Shared;
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
    private const string ClaimedSectionsProperty = "ClaimedSections";
    private const string CurrentStateProperty = "CurrentState";
    private const string LegacyStateProperty = "LegacyState";
    private const string LegacyFallbackDisabledProperty = "LegacyFallbackDisabled";

    private JsonObject _currentState = [];
    private JsonObject _legacyState = [];
    private JsonObject _legacyStateSnapshot = [];
    private readonly HashSet<string> _claimedSectionNames = new(StringComparer.Ordinal);
    private bool _legacyFallbackDisabled;

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
        var currentStatePath = GetStatePath(configuration, GetDeploymentStatePathSha(configuration), hostEnvironment.EnvironmentName);
        if (currentStatePath is null ||
            File.Exists(currentStatePath) ||
            File.Exists(GetMigrationStatePath(currentStatePath)))
        {
            return currentStatePath;
        }

        var legacyStatePath = GetStatePath(configuration, configuration["AppHost:LegacyDeploymentStatePathSha256"], hostEnvironment.EnvironmentName);
        return legacyStatePath is not null && File.Exists(legacyStatePath)
            ? legacyStatePath
            : currentStatePath;
    }

    private string? GetCanonicalStatePath() => GetStatePath(configuration, GetDeploymentStatePathSha(configuration), hostEnvironment.EnvironmentName);

    private string? GetLegacyStatePath() => GetStatePath(configuration, configuration["AppHost:LegacyDeploymentStatePathSha256"], hostEnvironment.EnvironmentName);

    private static string? GetDeploymentStatePathSha(IConfiguration configuration)
        => configuration["AppHost:DeploymentStatePathSha256"] ?? configuration["AppHost:PathSha256"];

    internal static string GetMigrationStatePath(string canonicalStatePath) =>
        $"{canonicalStatePath}.migration";

    internal static async Task<JsonObject> LoadEffectiveStateAsync(
        string canonicalStatePath,
        string? legacyStatePath,
        CancellationToken cancellationToken = default)
    {
        JsonObject currentState;
        MigrationState migrationState;
        using (await AcquireStateLockAsync(canonicalStatePath, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                migrationState = await LoadMigrationStateFileAsync(canonicalStatePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
            {
                return await LoadStateFileAsync(canonicalStatePath, cancellationToken).ConfigureAwait(false);
            }

            currentState = migrationState.CurrentState ??
                await LoadStateFileAsync(canonicalStatePath, cancellationToken).ConfigureAwait(false);
        }

        var (legacyFallbackDisabled, legacyStateSnapshot, claimedSectionNames, _) = migrationState;
        if (legacyFallbackDisabled)
        {
            return currentState;
        }

        JsonObject legacyState = [];
        // Only lock and read legacy state when the file actually exists. Acquiring the lock would
        // eagerly create the shared legacy directory and lock file for identities that never had
        // legacy state (e.g. brand-new source AppHosts), and would serialize sibling AppHosts that
        // share the legacy identity on that lock. Legacy state is immutable in this model, so an
        // existence check before locking is safe.
        if (!string.IsNullOrEmpty(legacyStatePath) && File.Exists(legacyStatePath))
        {
            try
            {
                using (await AcquireStateLockAsync(legacyStatePath, cancellationToken).ConfigureAwait(false))
                {
                    legacyState = await LoadStateFileAsync(legacyStatePath, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsUnavailableLegacyState(ex))
            {
                // Legacy state is optional fallback data. A malformed or unreadable shared file
                // must not hide usable canonical state.
            }
        }

        var effectiveState = MergeState(MergeState(legacyStateSnapshot, legacyState), currentState);
        ApplyClaimedSections(effectiveState, currentState, claimedSectionNames);

        return effectiveState;
    }

    internal static JsonObject LoadEffectiveState(string canonicalStatePath, string? legacyStatePath)
    {
        JsonObject currentState;
        MigrationState migrationState;
        using (AcquireStateLock(canonicalStatePath))
        {
            try
            {
                migrationState = LoadMigrationStateFile(canonicalStatePath);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
            {
                return LoadStateFile(canonicalStatePath);
            }

            currentState = migrationState.CurrentState ?? LoadStateFile(canonicalStatePath);
        }

        var (legacyFallbackDisabled, legacyStateSnapshot, claimedSectionNames, _) = migrationState;
        if (legacyFallbackDisabled)
        {
            return currentState;
        }

        JsonObject legacyState = [];
        // See LoadEffectiveStateAsync: skip locking when no legacy file exists so we do not create
        // phantom legacy directories/locks or re-couple sibling AppHosts on a shared lock.
        if (!string.IsNullOrEmpty(legacyStatePath) && File.Exists(legacyStatePath))
        {
            try
            {
                using (AcquireStateLock(legacyStatePath))
                {
                    legacyState = LoadStateFile(legacyStatePath);
                }
            }
            catch (Exception ex) when (IsUnavailableLegacyState(ex))
            {
                // Legacy state is optional fallback data. A malformed or unreadable shared file
                // must not hide usable canonical state.
            }
        }

        var effectiveState = MergeState(MergeState(legacyStateSnapshot, legacyState), currentState);
        ApplyClaimedSections(effectiveState, currentState, claimedSectionNames);

        return effectiveState;
    }

    internal static string? GetStatePath(IConfiguration configuration, string? appHostSha, string environmentName)
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
    protected override async Task<JsonObject> LoadStateFromStorageAsync(CancellationToken cancellationToken = default)
    {
        var currentStatePath = GetCanonicalStatePath();
        var legacyStatePath = GetLegacyStatePath();

        if (currentStatePath is not null)
        {
            using (await AcquireStateLockAsync(currentStatePath, cancellationToken).ConfigureAwait(false))
            {
                if (!await LoadMigrationStateAsync(currentStatePath, cancellationToken).ConfigureAwait(false))
                {
                    _currentState = await LoadStateFileAsync(currentStatePath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Skip locking when no legacy file exists so we do not create a phantom legacy directory and
        // lock for identities that never had legacy state (e.g. brand-new source AppHosts), and so
        // sibling AppHosts that share the legacy identity do not serialize on that shared lock.
        if (!_legacyFallbackDisabled &&
            legacyStatePath is not null &&
            !string.Equals(currentStatePath, legacyStatePath, StringComparison.Ordinal) &&
            File.Exists(legacyStatePath))
        {
            try
            {
                using (await AcquireStateLockAsync(legacyStatePath, cancellationToken).ConfigureAwait(false))
                {
                    _legacyState = await LoadStateFileAsync(legacyStatePath, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsUnavailableLegacyState(ex))
            {
                _legacyState = [];
            }
        }

        return MergeState(GetLegacyFallbackState(), _currentState);
    }

    /// <inheritdoc/>
    protected override JsonNode? GetSectionState(JsonObject? state, string sectionName, bool includeLegacyState)
    {
        if (!includeLegacyState)
        {
            return TryGetNestedPropertyValue(_currentState, sectionName);
        }

        if (_legacyFallbackDisabled ||
            _claimedSectionNames.Any(name =>
                string.Equals(name, sectionName, StringComparison.Ordinal) ||
                sectionName.StartsWith($"{name}:", StringComparison.Ordinal)))
        {
            return TryGetNestedPropertyValue(_currentState, sectionName);
        }

        var mergedSection = base.GetSectionState(state, sectionName, includeLegacyState)?.DeepClone();
        foreach (var claimedSectionName in _claimedSectionNames.Where(name => name.StartsWith($"{sectionName}:", StringComparison.Ordinal)))
        {
            if (mergedSection is not JsonObject mergedObject)
            {
                continue;
            }

            var relativeSectionName = claimedSectionName[(sectionName.Length + 1)..];
            var currentValueExists = NestedPropertyExists(_currentState, claimedSectionName);
            SetNestedNodeValue(
                mergedObject,
                relativeSectionName,
                currentValueExists ? TryGetNestedPropertyValue(_currentState, claimedSectionName)?.DeepClone() : null,
                currentValueExists);
        }

        return mergedSection;
    }

    /// <inheritdoc/>
    protected override async Task SaveStateToStorageAsync(
        JsonObject state,
        string? sectionName,
        JsonObject? originalSectionData,
        CancellationToken cancellationToken)
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

            using (await AcquireStateLockAsync(deploymentStatePath, cancellationToken).ConfigureAwait(false))
            {
                if (sectionName is null)
                {
                    // Normalize through the same persisted representation used by WriteStateAsync
                    // so backward-compatible flattened input and nested input have identical
                    // authoritative sidecar state.
                    _currentState = JsonFlattener.UnflattenJsonObject(JsonFlattener.FlattenJsonObject(state));
                    _legacyState = [];
                    _legacyStateSnapshot = [];
                    _claimedSectionNames.Clear();
                    _legacyFallbackDisabled = true;
                }
                else
                {
                    // Reload while holding the cross-process lock so repeated or concurrent
                    // process starts cannot overwrite sections saved by another instance.
                    if (!await LoadMigrationStateAsync(deploymentStatePath, cancellationToken).ConfigureAwait(false))
                    {
                        _currentState = await LoadStateFileAsync(deploymentStatePath, cancellationToken).ConfigureAwait(false);
                    }
                    var sectionData = TryGetNestedPropertyValue(state, sectionName) as JsonObject;
                    var legacySectionData = TryGetNestedPropertyValue(GetLegacyFallbackState(), sectionName);
                    var latestEffectiveState = MergeState(GetLegacyFallbackState(), _currentState);
                    ApplyClaimedSections(latestEffectiveState, _currentState, _claimedSectionNames);
                    var latestSectionData = NormalizeSectionData(
                        TryGetNestedPropertyValue(latestEffectiveState, sectionName));
                    var desiredSectionData = ApplyChanges(latestSectionData, originalSectionData, sectionData) as JsonObject;
                    if (TryGetNestedPropertyValue(_legacyStateSnapshot, sectionName) is null &&
                        legacySectionData is not null)
                    {
                        SetNestedNodeValue(_legacyStateSnapshot, sectionName, legacySectionData.DeepClone(), valueExists: true);
                    }
                    SetNestedPropertyValue(_currentState, sectionName, null);
                    _claimedSectionNames.RemoveWhere(name =>
                        string.Equals(name, sectionName, StringComparison.Ordinal) ||
                        name.StartsWith($"{sectionName}:", StringComparison.Ordinal));
                    ApplyStateDelta(_currentState, sectionName, legacySectionData, desiredSectionData, _claimedSectionNames);
                }

                await SaveMigrationStateAsync(deploymentStatePath, cancellationToken).ConfigureAwait(false);
                await WriteStateAsync(deploymentStatePath, _currentState, cancellationToken).ConfigureAwait(false);
            }

            logger.LogDebug("Deployment state saved to {Path}", deploymentStatePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save deployment state.");
            throw;
        }
    }

    private static JsonObject? NormalizeSectionData(JsonNode? sectionData) =>
        sectionData switch
        {
            JsonObject sectionObject => sectionObject,
            JsonValue sectionValue when sectionValue.GetValueKind() == JsonValueKind.String =>
                new JsonObject { [""] = sectionValue.DeepClone() },
            _ => null
        };

    /// <inheritdoc/>
    protected override async Task ClearStateStorageAsync(CancellationToken cancellationToken)
    {
        var currentStatePath = GetCanonicalStatePath();
        if (currentStatePath is null)
        {
            _currentState = [];
            _legacyState = [];
            _legacyStateSnapshot = [];
            _claimedSectionNames.Clear();
            _legacyFallbackDisabled = true;
            return;
        }

        using (await AcquireStateLockAsync(currentStatePath, cancellationToken).ConfigureAwait(false))
        {
            await SaveMigrationStateAsync(
                currentStatePath,
                currentState: [],
                legacyStateSnapshot: [],
                claimedSectionNames: [],
                legacyFallbackDisabled: true,
                cancellationToken).ConfigureAwait(false);

            // Publish the cleared in-memory view only after the sidecar durably makes empty current
            // state authoritative. If deleting the superseded state file then fails, readers still
            // observe the committed empty sidecar state.
            _currentState = [];
            _legacyState = [];
            _legacyStateSnapshot = [];
            _claimedSectionNames.Clear();
            _legacyFallbackDisabled = true;

            if (File.Exists(currentStatePath))
            {
                File.Delete(currentStatePath);
                logger.LogInformation("Deployment state cleared: {Path}", currentStatePath);
            }
        }
    }

    private static JsonObject MergeState(JsonObject legacyState, JsonObject currentState)
    {
        var mergedState = legacyState.DeepClone().AsObject();
        MergeInto(mergedState, currentState);
        return mergedState;

        static void MergeInto(JsonObject target, JsonObject source)
        {
            foreach (var (name, sourceValue) in source)
            {
                if (sourceValue is JsonObject sourceObject &&
                    target[name] is JsonObject targetObject)
                {
                    MergeInto(targetObject, sourceObject);
                }
                else
                {
                    target[name] = sourceValue?.DeepClone();
                }
            }
        }
    }

    private JsonObject GetLegacyFallbackState() =>
        _legacyFallbackDisabled ? [] : MergeState(_legacyStateSnapshot, _legacyState);

    private static async Task WriteStateAsync(string statePath, JsonObject state, CancellationToken cancellationToken)
    {
        var flattenedSecrets = JsonFlattener.FlattenJsonObject(state);
        var deploymentStateDirectory = Path.GetDirectoryName(statePath)!;
        DirectoryHelper.CreateWithOwnerOnlyPermissions(deploymentStateDirectory);
        var temporaryStatePath = Path.Combine(
            deploymentStateDirectory,
            $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryStatePath,
                flattenedSecrets.ToJsonString(UserSecretsJsonOptions.s_instance),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryStatePath, statePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryStatePath);
        }
    }

    private static FileLock AcquireStateLock(string statePath)
    {
        EnsureStateDirectoryPermissions(statePath);
        return FileLock.Acquire($"{statePath}.lock", TimeSpan.FromMinutes(5));
    }

    private static async Task<FileLock> AcquireStateLockAsync(
        string statePath,
        CancellationToken cancellationToken)
    {
        EnsureStateDirectoryPermissions(statePath);
        return await FileLock.AcquireAsync($"{statePath}.lock", cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureStateDirectoryPermissions(string statePath)
    {
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        DirectoryHelper.CreateWithOwnerOnlyPermissions(stateDirectory);
    }

    private static void ApplyStateDelta(
        JsonObject currentState,
        string sectionName,
        JsonNode? legacyValue,
        JsonNode? savedValue,
        HashSet<string> claimedSectionNames)
    {
        ApplyDelta(
            sectionName,
            legacyValue,
            baselineExists: legacyValue is not null,
            savedValue,
            savedExists: savedValue is not null);

        void ApplyDelta(
            string path,
            JsonNode? baseline,
            bool baselineExists,
            JsonNode? value,
            bool savedExists)
        {
            if (baseline is JsonObject baselineObject && value is JsonObject valueObject)
            {
                foreach (var propertyName in baselineObject.Select(static pair => pair.Key)
                    .Union(valueObject.Select(static pair => pair.Key), StringComparer.Ordinal))
                {
                    var baselinePropertyExists = baselineObject.TryGetPropertyValue(propertyName, out var baselineProperty);
                    var savedPropertyExists = valueObject.TryGetPropertyValue(propertyName, out var valueProperty);
                    ApplyDelta(
                        $"{path}:{propertyName}",
                        baselineProperty,
                        baselinePropertyExists,
                        valueProperty,
                        savedPropertyExists);
                }

                return;
            }

            if (baselineExists == savedExists && JsonNode.DeepEquals(baseline, value))
            {
                return;
            }

            SetNestedNodeValue(currentState, path, value?.DeepClone(), savedExists);
            claimedSectionNames.Add(path);
        }
    }

    private static JsonNode? ApplyChanges(JsonNode? latestValue, JsonNode? originalValue, JsonNode? savedValue)
    {
        if (originalValue is JsonObject originalObject &&
            savedValue is JsonObject savedObject &&
            !IsScalarSectionData(originalObject) &&
            !IsScalarSectionData(savedObject) &&
            (latestValue is not JsonObject latestObject || !IsScalarSectionData(latestObject)))
        {
            var result = latestValue?.DeepClone() as JsonObject ?? [];
            foreach (var propertyName in originalObject.Select(static pair => pair.Key)
                .Union(savedObject.Select(static pair => pair.Key), StringComparer.Ordinal))
            {
                var originalPropertyExists = originalObject.TryGetPropertyValue(propertyName, out var originalProperty);
                var savedPropertyExists = savedObject.TryGetPropertyValue(propertyName, out var savedProperty);
                var latestPropertyExists = result.TryGetPropertyValue(propertyName, out var latestProperty);
                var updatedProperty = ApplyPropertyChanges(
                    latestProperty,
                    latestPropertyExists,
                    originalProperty,
                    originalPropertyExists,
                    savedProperty,
                    savedPropertyExists);
                if (!updatedProperty.Exists)
                {
                    result.Remove(propertyName);
                }
                else
                {
                    result[propertyName] = updatedProperty.Value;
                }
            }

            return result;
        }

        return JsonNode.DeepEquals(originalValue, savedValue)
            ? latestValue?.DeepClone()
            : savedValue?.DeepClone();

        static (bool Exists, JsonNode? Value) ApplyPropertyChanges(
            JsonNode? latestValue,
            bool latestExists,
            JsonNode? originalValue,
            bool originalExists,
            JsonNode? savedValue,
            bool savedExists)
        {
            if (originalExists == savedExists && JsonNode.DeepEquals(originalValue, savedValue))
            {
                return (latestExists, latestValue?.DeepClone());
            }

            if (savedValue is JsonObject savedObject &&
                (originalValue is JsonObject || !originalExists) &&
                !IsScalarSectionData(savedObject) &&
                (originalValue is not JsonObject originalObject || !IsScalarSectionData(originalObject)) &&
                (latestValue is not JsonObject latestObject || !IsScalarSectionData(latestObject)))
            {
                return (true, ApplyChanges(latestValue, originalValue as JsonObject ?? [], savedObject));
            }

            return savedExists
                ? (true, savedValue?.DeepClone())
                : (false, null);
        }
    }

    private static bool IsScalarSectionData(JsonObject value) => value.ContainsKey(string.Empty);

    private static bool IsUnavailableLegacyState(Exception exception) =>
        exception is JsonException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static void ApplyClaimedSections(
        JsonObject effectiveState,
        JsonObject currentState,
        IEnumerable<string> claimedSectionNames)
    {
        foreach (var sectionName in claimedSectionNames)
        {
            if (NestedPropertyExists(currentState, sectionName))
            {
                SetNestedNodeValue(
                    effectiveState,
                    sectionName,
                    TryGetNestedPropertyValue(currentState, sectionName)?.DeepClone(),
                    valueExists: true);
            }
            else
            {
                SetNestedNodeValue(effectiveState, sectionName, value: null, valueExists: false);
            }
        }
    }

    private static bool NestedPropertyExists(JsonObject root, string path)
    {
        var segments = path.Split(':');
        JsonObject current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var nextNode) ||
                nextNode is not JsonObject nextObject)
            {
                return false;
            }

            current = nextObject;
        }

        return current.ContainsKey(segments[^1]);
    }

    private static void SetNestedNodeValue(JsonObject root, string path, JsonNode? value, bool valueExists)
    {
        var segments = path.Split(':');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (!current.TryGetPropertyValue(segment, out var nextNode) || nextNode is not JsonObject nextObject)
            {
                if (!valueExists)
                {
                    return;
                }

                nextObject = [];
                current[segment] = nextObject;
            }

            current = nextObject;
        }

        if (!valueExists)
        {
            current.Remove(segments[^1]);
        }
        else
        {
            current[segments[^1]] = value;
        }
    }

    private async Task<bool> LoadMigrationStateAsync(string? canonicalStatePath, CancellationToken cancellationToken)
    {
        _claimedSectionNames.Clear();
        _legacyStateSnapshot = [];
        _legacyFallbackDisabled = false;
        if (canonicalStatePath is null)
        {
            return false;
        }

        try
        {
            var migrationState = await LoadMigrationStateFileAsync(canonicalStatePath, cancellationToken).ConfigureAwait(false);
            _legacyFallbackDisabled = migrationState.LegacyFallbackDisabled;
            var legacyStateSnapshot = migrationState.LegacyStateSnapshot;
            _legacyStateSnapshot = legacyStateSnapshot;
            _claimedSectionNames.UnionWith(migrationState.ClaimedSectionNames);
            if (migrationState.CurrentState is not null)
            {
                _currentState = migrationState.CurrentState;
                return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        {
            // Migration metadata controls whether ambiguous shared state can be adopted.
            // If it is malformed, fail closed rather than reviving legacy deployment state.
            _legacyFallbackDisabled = true;
        }

        return false;
    }

    private static async Task<MigrationState> LoadMigrationStateFileAsync(
        string canonicalStatePath,
        CancellationToken cancellationToken)
    {
        var migrationStatePath = GetMigrationStatePath(canonicalStatePath);
        if (!File.Exists(migrationStatePath))
        {
            return new(false, [], [], null);
        }

        var migrationState = await LoadStateFileAsync(migrationStatePath, cancellationToken).ConfigureAwait(false);
        return ParseMigrationState(migrationState);
    }

    private static MigrationState LoadMigrationStateFile(string canonicalStatePath)
    {
        var migrationStatePath = GetMigrationStatePath(canonicalStatePath);
        return File.Exists(migrationStatePath)
            ? ParseMigrationState(LoadStateFile(migrationStatePath))
            : new(false, [], [], null);
    }

    private static MigrationState ParseMigrationState(JsonObject migrationState)
    {
        var legacyFallbackDisabled = migrationState[LegacyFallbackDisabledProperty]?.GetValue<bool>() ?? false;
        var legacyStateSnapshot = migrationState[LegacyStateProperty] switch
        {
            null => [],
            JsonObject legacyState => legacyState,
            _ => throw new InvalidDataException($"'{LegacyStateProperty}' must be a JSON object.")
        };
        var claimedSectionNames = migrationState[ClaimedSectionsProperty] switch
        {
            null => [],
            JsonValue claimedSectionsValue => ParseClaimedSectionNames(claimedSectionsValue),
            _ => throw new InvalidDataException($"'{ClaimedSectionsProperty}' must be a JSON string.")
        };
        var currentState = migrationState[CurrentStateProperty] switch
        {
            null => null,
            JsonValue stateValue => JsonNode.Parse(stateValue.GetValue<string>())?.AsObject()
                ?? throw new InvalidDataException($"'{CurrentStateProperty}' must contain a JSON object."),
            _ => throw new InvalidDataException($"'{CurrentStateProperty}' must be a JSON string.")
        };

        return new(legacyFallbackDisabled, legacyStateSnapshot, claimedSectionNames, currentState);
    }

    private static string[] ParseClaimedSectionNames(JsonValue claimedSectionsValue)
    {
        var claimedSectionNames = JsonSerializer.Deserialize<string?[]>(
            claimedSectionsValue.GetValue<string>());
        if (claimedSectionNames is null ||
            claimedSectionNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"'{ClaimedSectionsProperty}' must contain a JSON array of non-empty strings.");
        }

        return [.. claimedSectionNames.Select(static sectionName => sectionName!)];
    }

    private Task SaveMigrationStateAsync(string canonicalStatePath, CancellationToken cancellationToken) =>
        SaveMigrationStateAsync(
            canonicalStatePath,
            _currentState,
            _legacyStateSnapshot,
            _claimedSectionNames,
            _legacyFallbackDisabled,
            cancellationToken);

    private static Task SaveMigrationStateAsync(
        string canonicalStatePath,
        JsonObject currentState,
        JsonObject legacyStateSnapshot,
        IEnumerable<string> claimedSectionNames,
        bool legacyFallbackDisabled,
        CancellationToken cancellationToken)
    {
        var migrationState = new JsonObject
        {
            [ClaimedSectionsProperty] = JsonSerializer.Serialize(
                claimedSectionNames.Order(StringComparer.Ordinal)),
            [CurrentStateProperty] = currentState.ToJsonString(),
            [LegacyStateProperty] = legacyStateSnapshot.DeepClone(),
            [LegacyFallbackDisabledProperty] = legacyFallbackDisabled
        };

        return WriteStateAsync(GetMigrationStatePath(canonicalStatePath), migrationState, cancellationToken);
    }

    private sealed record MigrationState(
        bool LegacyFallbackDisabled,
        JsonObject LegacyStateSnapshot,
        string[] ClaimedSectionNames,
        JsonObject? CurrentState);
}
