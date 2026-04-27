// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.Internal;

/// <summary>
/// Base class for deployment state managers that provides common functionality for state management,
/// concurrency control, and section-based locking.
/// </summary>
/// <typeparam name="T">The type of the derived class for logger typing.</typeparam>
/// <remarks>
/// Initializes a new instance of the <see cref="DeploymentStateManagerBase{T}"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
internal abstract class DeploymentStateManagerBase<T>(ILogger<T> logger) : IDeploymentStateManager where T : class
{
    /// <summary>
    /// Holds section metadata including version information.
    /// </summary>
    private sealed class SectionMetadata(long version)
    {
        public long Version { get; } = version;
    }

    /// <summary>
    /// JSON serializer options used for writing deployment state files.
    /// </summary>
    protected static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Logger instance for the derived class.
    /// </summary>
    protected readonly ILogger<T> logger = logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _sectionsLock = new();
    private readonly Dictionary<string, SectionMetadata> _sections = new();
    private JsonObject? _state;
    private bool _isStateLoaded;

    /// <inheritdoc/>
    public abstract string? StateFilePath { get; }

    /// <summary>
    /// Gets the path where the state file should be stored. Returns null if the path cannot be determined.
    /// </summary>
    protected abstract string? GetStatePath();

    /// <summary>
    /// Saves the state to the appropriate storage location.
    /// </summary>
    /// <param name="state">The state to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task SaveStateToStorageAsync(JsonObject state, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the deployment state from storage, using caching to avoid repeated loads.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded state as a JsonObject.</returns>
    protected async Task<JsonObject> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStateLoaded && _state is not null)
            {
                return _state;
            }

            var jsonDocumentOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            var statePath = GetStatePath();

            if (statePath is not null && File.Exists(statePath))
            {
                var fileContent = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
                var flattenedState = JsonNode.Parse(fileContent, documentOptions: jsonDocumentOptions)!.AsObject();
                _state = JsonFlattener.UnflattenJsonObject(flattenedState);
            }
            else
            {
                _state = [];
            }

            _isStateLoaded = true;
            return _state;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveStateToStorageAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private SectionMetadata GetSectionMetadata(string sectionName)
    {
        lock (_sectionsLock)
        {
            if (!_sections.TryGetValue(sectionName, out var metadata))
            {
                metadata = new SectionMetadata(0);
                _sections[sectionName] = metadata;
            }
            return metadata;
        }
    }

    /// <inheritdoc/>
    public async Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        await LoadStateAsync(cancellationToken).ConfigureAwait(false);

        var metadata = GetSectionMetadata(sectionName);

        // Protect access to _state with _stateLock to prevent concurrent modification during enumeration
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject? data = null;
            string? value = null;

            var sectionData = TryGetNestedPropertyValue(_state, sectionName);
            if (sectionData is JsonObject o)
            {
                data = o.DeepClone().AsObject();
            }
            else if (sectionData is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
            {
                // This handles the situation where the section is just a string value.
                value = jsonValue.GetValue<string>();
            }

            var section = new DeploymentStateSection(sectionName, data, metadata.Version);
            if (value != null)
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

    /// <summary>
    /// Recursively navigates a JSON object using a colon-separated path.
    /// </summary>
    /// <param name="node">The starting JSON object.</param>
    /// <param name="path">The colon-separated path to navigate.</param>
    /// <returns>The JSON node at the specified path, or null if not found.</returns>
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

    /// <inheritdoc/>
    public async Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        await EnsureStateAndSectionAsync(section, cancellationToken).ConfigureAwait(false);
        Debug.Assert(_state is not null);

        // Serialize state modification and file write to prevent concurrent enumeration
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Store a deep clone to ensure immutability
            SetNestedPropertyValue(_state, section.SectionName, section.Data.DeepClone().AsObject());
            await SaveStateToStorageAsync(_state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        await EnsureStateAndSectionAsync(section, cancellationToken).ConfigureAwait(false);
        Debug.Assert(_state is not null);

        // Serialize state modification and file write to prevent concurrent enumeration
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Remove the section from the state by passing null
            SetNestedPropertyValue(_state, section.SectionName, null);
            await SaveStateToStorageAsync(_state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task EnsureStateAndSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken)
    {
        await LoadStateAsync(cancellationToken).ConfigureAwait(false);

        if (_state is null)
        {
            throw new InvalidOperationException("State has not been loaded.");
        }

        // Atomically check version and update using lock + Dictionary
        lock (_sectionsLock)
        {
            if (_sections.TryGetValue(section.SectionName, out var metadata))
            {
                if (metadata.Version != section.Version)
                {
                    throw new InvalidOperationException(
                        $"Concurrency conflict detected in section '{section.SectionName}'. " +
                        $"Expected version {section.Version}, but current version is {metadata.Version}. " +
                        $"This typically indicates the section was modified after it was acquired. " +
                        $"Ensure the section is saved before being modified by another operation.");
                }
            }

            // Create new metadata with incremented version
            _sections[section.SectionName] = new SectionMetadata(section.Version + 1);
        }

        // Increment the section's version to allow multiple saves with the same instance
        section.Version++;
    }

    /// <summary>
    /// Sets or removes a value in a JSON object using a colon-separated path, creating intermediate objects as needed.
    /// </summary>
    /// <param name="root">The root JSON object.</param>
    /// <param name="path">The colon-separated path to set.</param>
    /// <param name="value">The value to set at the specified path, or null to remove the property.</param>
    private static void SetNestedPropertyValue(JsonObject root, string path, JsonObject? value)
    {
        var segments = path.Split(':');

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (!current.TryGetPropertyValue(segment, out var nextNode) || nextNode is not JsonObject nextObj)
            {
                // If removing and the path doesn't exist, nothing to do
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

    /// <inheritdoc/>
    public async Task ClearAllStateAsync(CancellationToken cancellationToken = default)
    {
        if (StateFilePath is string stateFilePath && File.Exists(stateFilePath))
        {
            File.Delete(stateFilePath);
            logger.LogInformation("Deployment state cleared: {Path}", stateFilePath);
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sectionsLock)
            {
                _sections.Clear();
            }
            _state = null;
            _isStateLoaded = false;
        }
        finally
        {
            _stateLock.Release();
        }
    }
}
