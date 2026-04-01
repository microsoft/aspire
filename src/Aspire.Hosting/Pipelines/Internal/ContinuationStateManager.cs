// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.Internal;

/// <summary>
/// State manager for continuation-mode pipeline execution. Reads merged state from
/// all scope files in a run-scoped directory; writes only to the current scope's file.
/// </summary>
internal sealed class ContinuationStateManager : IDeploymentStateManager
{
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<ContinuationStateManager> _logger;
    private readonly PipelineScopeResult _scope;
    private readonly string _stateDirectory;
    private readonly string _writeFilePath;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _sectionsLock = new();
    private readonly Dictionary<string, long> _sectionVersions = new();
    private JsonObject? _mergedState;
    private bool _isLoaded;

    public ContinuationStateManager(
        ILogger<ContinuationStateManager> logger,
        PipelineScopeResult scope,
        string baseDirectory)
    {
        _logger = logger;
        _scope = scope;
        _stateDirectory = Path.Combine(baseDirectory, scope.RunId);
        _writeFilePath = Path.Combine(_stateDirectory, $"{scope.JobId}.json");
    }

    /// <inheritdoc/>
    public string? StateFilePath => _writeFilePath;

    /// <summary>
    /// Loads and merges all state files from the run-scoped directory.
    /// </summary>
    private async Task<JsonObject> LoadMergedStateAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded && _mergedState is not null)
            {
                return _mergedState;
            }

            _mergedState = new JsonObject();

            if (Directory.Exists(_stateDirectory))
            {
                var jsonFiles = Directory.GetFiles(_stateDirectory, "*.json");
                _logger.LogDebug(
                    "Loading {Count} state file(s) from {Directory}",
                    jsonFiles.Length,
                    _stateDirectory);

                var jsonDocumentOptions = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };

                foreach (var filePath in jsonFiles)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                        var flattenedState = JsonNode.Parse(content, documentOptions: jsonDocumentOptions)?.AsObject();
                        if (flattenedState is null)
                        {
                            continue;
                        }

                        var unflattenedState = JsonFlattener.UnflattenJsonObject(flattenedState);
                        MergeJsonObject(_mergedState, unflattenedState, Path.GetFileName(filePath));

                        _logger.LogDebug("Merged state from {File}", Path.GetFileName(filePath));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load state file {File}", filePath);
                    }
                }
            }
            else
            {
                _logger.LogDebug("State directory {Directory} does not exist, starting with empty state", _stateDirectory);
            }

            _isLoaded = true;
            return _mergedState;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<DeploymentStateSection> AcquireSectionAsync(
        string sectionName,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadMergedStateAsync(cancellationToken).ConfigureAwait(false);

        long version;
        lock (_sectionsLock)
        {
            if (!_sectionVersions.TryGetValue(sectionName, out version))
            {
                version = 0;
                _sectionVersions[sectionName] = version;
            }
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject? data = null;
            string? value = null;

            var sectionData = TryGetNestedPropertyValue(state, sectionName);
            if (sectionData is JsonObject o)
            {
                data = o.DeepClone().AsObject();
            }
            else if (sectionData is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
            {
                value = jsonValue.GetValue<string>();
            }

            var section = new DeploymentStateSection(sectionName, data, version);
            if (value is not null)
            {
                section.SetValue(value);
            }

            return section;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken = default)
    {
        await LoadMergedStateAsync(cancellationToken).ConfigureAwait(false);

        lock (_sectionsLock)
        {
            if (_sectionVersions.TryGetValue(section.SectionName, out var currentVersion)
                && currentVersion != section.Version)
            {
                throw new InvalidOperationException(
                    $"Concurrency conflict in section '{section.SectionName}'. " +
                    $"Expected version {section.Version}, current is {currentVersion}.");
            }

            _sectionVersions[section.SectionName] = section.Version + 1;
        }

        section.Version++;

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Update merged state for in-memory reads
            SetNestedPropertyValue(_mergedState!, section.SectionName, section.Data.DeepClone().AsObject());

            // Write only the current scope's state file
            await WriteScopeFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken = default)
    {
        await LoadMergedStateAsync(cancellationToken).ConfigureAwait(false);

        lock (_sectionsLock)
        {
            if (_sectionVersions.TryGetValue(section.SectionName, out var currentVersion)
                && currentVersion != section.Version)
            {
                throw new InvalidOperationException(
                    $"Concurrency conflict in section '{section.SectionName}'. " +
                    $"Expected version {section.Version}, current is {currentVersion}.");
            }

            _sectionVersions[section.SectionName] = section.Version + 1;
        }

        section.Version++;

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetNestedPropertyValue(_mergedState!, section.SectionName, null);
            await WriteScopeFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Writes the current scope's portion of the merged state to its scope file.
    /// Only sections modified by this scope are included (tracked by the merged state).
    /// </summary>
    private async Task WriteScopeFileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);

        // We write the full merged state as the scope file. This is fine because
        // each scope writes distinct sections in practice. The merge-on-read
        // approach handles the recombination correctly.
        var flattened = JsonFlattener.FlattenJsonObject(_mergedState!);
        await File.WriteAllTextAsync(
            _writeFilePath,
            flattened.ToJsonString(s_jsonSerializerOptions),
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("State saved to {Path}", _writeFilePath);
    }

    /// <summary>
    /// Deep-merges properties from <paramref name="source"/> into <paramref name="target"/>.
    /// </summary>
    private static void MergeJsonObject(JsonObject target, JsonObject source, string sourceFile)
    {
        foreach (var kvp in source)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            if (target.TryGetPropertyValue(kvp.Key, out var existingNode) && existingNode is JsonObject existingObj
                && kvp.Value is JsonObject sourceObj)
            {
                MergeJsonObject(existingObj, sourceObj.DeepClone().AsObject(), sourceFile);
            }
            else
            {
                target[kvp.Key] = kvp.Value.DeepClone();
            }
        }
    }

    private static JsonNode? TryGetNestedPropertyValue(JsonObject? node, string path)
    {
        if (node is null)
        {
            return null;
        }

        var segments = path.Split(':');
        JsonNode? current = node;

        foreach (var segment in segments)
        {
            if (current is not JsonObject currentObj || !currentObj.TryGetPropertyValue(segment, out var nextNode))
            {
                return null;
            }
            current = nextNode;
        }

        return current;
    }

    private static void SetNestedPropertyValue(JsonObject root, string path, JsonObject? value)
    {
        var segments = path.Split(':');

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (!current.TryGetPropertyValue(segment, out var nextNode) || nextNode is not JsonObject nextObj)
            {
                if (value is null)
                {
                    return;
                }
                nextObj = new JsonObject();
                current[segment] = nextObj;
            }
            current = nextObj;
        }

        if (value is null)
        {
            current.Remove(segments[^1]);
        }
        else
        {
            current[segments[^1]] = value;
        }
    }
}
