// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores the tags users assign to resources.
/// </summary>
/// <remarks>
/// <para>
/// Tags describe the application rather than a single run, so they're kept in the browser's local storage under a key
/// scoped to the application name, and every run of the application shows them. They aren't shared between browsers.
/// Tags are keyed by the resource's display name because replica names have a random suffix that changes on every
/// run; a tag therefore applies to every replica of a resource.
/// </para>
/// <para>
/// The store is scoped to a circuit. Reads are synchronous and return nothing until <see cref="EnsureLoadedAsync"/>
/// has read the stored tags, which raises <see cref="Changed"/> so views showing tags update.
/// </para>
/// </remarks>
public sealed class ResourceTagStore
{
    internal const int MaxTagLength = 50;

    private readonly Lock _lock = new();
    private readonly LocalStorageSetting<Dictionary<string, List<string>>> _setting;
    private readonly Dictionary<string, List<string>> _tagsByResource = new(StringComparers.ResourceName);
    private Task? _loadTask;
    private bool _loaded;

    public ResourceTagStore(ILocalStorage localStorage, IOptions<DashboardOptions> options, ILogger<ResourceTagStore> logger)
    {
        _setting = new(localStorage, BrowserStorageKeys.ResourceTagsKey(options.Value.GetApplicationNameOrDefault()), logger);
    }

    /// <summary>
    /// Raised after tags are loaded, added or removed. Handlers can be invoked on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored tags, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    /// <summary>
    /// Gets the tags of a resource in the order they were added.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    public IReadOnlyList<string> GetTags(string resourceName)
    {
        lock (_lock)
        {
            return _tagsByResource.TryGetValue(resourceName, out var tags) ? tags.ToArray() : [];
        }
    }

    /// <summary>
    /// Gets every tag assigned to at least one resource, ordered by name.
    /// </summary>
    public IReadOnlyList<string> GetAllTags()
    {
        lock (_lock)
        {
            return GetAllTagsCore().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    /// <summary>
    /// Gets a snapshot of the tags of every tagged resource, keyed by resource display name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByResource()
    {
        lock (_lock)
        {
            return _tagsByResource.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value.ToArray(), StringComparers.ResourceName);
        }
    }

    /// <summary>Gets editable tags merged with immutable tags in the selected run's resource snapshots.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByResource(IEnumerable<ResourceViewModel> resources)
    {
        var tagsByResource = new Dictionary<string, IReadOnlyList<string>>(GetTagsByResource(), StringComparers.ResourceName);
        foreach (var resource in resources)
        {
            var appHostTags = resource.AppHostTags;
            if (appHostTags.Count > 0)
            {
                tagsByResource[resource.DisplayName] = appHostTags
                    .Concat(tagsByResource.GetValueOrDefault(resource.DisplayName) ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        return tagsByResource;
    }

    public IReadOnlyList<string> GetAllTags(IEnumerable<ResourceViewModel> resources) =>
        GetTagsByResource(resources).Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>
    /// Adds a tag to a resource. A tag that matches an existing tag ignoring case reuses the existing tag's casing.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    /// <param name="tag">The tag to add.</param>
    /// <returns><see langword="true"/> when the tag was added; otherwise, <see langword="false"/> when the tag is empty or already assigned.</returns>
    public bool AddTag(string resourceName, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        if (NormalizeTag(tag) is not { } normalizedTag)
        {
            return false;
        }

        lock (_lock)
        {

            // Reusing the casing of an existing tag keeps a single group per tag in the tag list.
            normalizedTag = GetAllTagsCore().FirstOrDefault(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)) ?? normalizedTag;

            if (!_tagsByResource.TryGetValue(resourceName, out var tags))
            {
                tags = [];
                _tagsByResource[resourceName] = tags;
            }
            else if (tags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            tags.Add(normalizedTag);
            SaveLocked();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes a tag from a resource.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    /// <param name="tag">The tag to remove.</param>
    /// <returns><see langword="true"/> when the tag was removed; otherwise, <see langword="false"/>.</returns>
    public bool RemoveTag(string resourceName, string tag)
    {
        lock (_lock)
        {

            if (!_tagsByResource.TryGetValue(resourceName, out var tags) ||
                tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            if (tags.Count == 0)
            {
                _tagsByResource.Remove(resourceName);
            }

            SaveLocked();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Trims a tag and collapses inner whitespace so visually identical tags are equal.
    /// </summary>
    /// <returns>The normalized tag, or <see langword="null"/> when the tag is empty.</returns>
    internal static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var builder = new StringBuilder(tag.Length);
        var pendingSpace = false;
        foreach (var character in tag.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var normalized = builder.ToString();
        return normalized.Length > MaxTagLength ? normalized[..MaxTagLength].TrimEnd() : normalized;
    }

    private IEnumerable<string> GetAllTagsCore() =>
        _tagsByResource.Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase);

    private async Task LoadAsync()
    {
        var stored = await _setting.ReadAsync().ConfigureAwait(false);
        var changed = false;
        lock (_lock)
        {
            _loaded = true;

            // Tags added before the stored tags were read are kept after the stored ones, then saved, so neither
            // replaces the other.
            var addedBeforeLoad = _tagsByResource.Count > 0;
            var current = _tagsByResource.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparers.ResourceName);
            _tagsByResource.Clear();
            foreach (var (resourceName, tags) in (stored ?? []).Concat(current))
            {
                if (string.IsNullOrEmpty(resourceName) || tags is null)
                {
                    continue;
                }

                foreach (var tag in tags)
                {
                    if (NormalizeTag(tag) is not { } normalizedTag)
                    {
                        continue;
                    }

                    if (!_tagsByResource.TryGetValue(resourceName, out var resourceTags))
                    {
                        resourceTags = [];
                        _tagsByResource[resourceName] = resourceTags;
                    }
                    if (!resourceTags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
                    {
                        resourceTags.Add(normalizedTag);
                        changed = true;
                    }
                }
            }

            if (addedBeforeLoad)
            {
                SaveLocked();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void SaveLocked()
    {
        // Until the stored tags are read, saving would replace them. LoadAsync saves changes made before then.
        if (!_loaded)
        {
            return;
        }

        _setting.Write(_tagsByResource.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparers.ResourceName));
    }
}
