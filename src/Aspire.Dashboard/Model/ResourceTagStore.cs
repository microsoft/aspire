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
/// has read the stored tags, which raises <see cref="Changed"/> so views showing tags update. Changes are merged with
/// tags saved by other tabs of the same browser rather than replacing them; see <see cref="LocalStorageSetting{TValue}"/>.
/// </para>
/// </remarks>
public sealed class ResourceTagStore
{
    internal const int MaxTagLength = 50;

    private readonly LocalStorageSetting<Dictionary<string, List<string>>> _setting;

    public ResourceTagStore(ILocalStorage localStorage, LocalStorageLocks locks, TimeProvider timeProvider, IOptions<DashboardOptions> options, ILogger<ResourceTagStore> logger)
    {
        _setting = new(localStorage, locks, timeProvider, BrowserStorageKeys.ResourceTagsKey(options.Value.GetApplicationNameOrDefault()), logger,
            NormalizeTags, TagsEqual, () => Changed?.Invoke());
    }

    /// <summary>
    /// Raised after tags are loaded, added or removed. Handlers can be invoked on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored tags, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _setting.EnsureLoadedAsync();

    /// <summary>
    /// Gets the tags of a resource in the order they were added.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    public IReadOnlyList<string> GetTags(string resourceName) =>
        _setting.Read(tagsByResource => tagsByResource.TryGetValue(resourceName, out var tags) ? tags.ToArray() : []);

    /// <summary>
    /// Gets every tag assigned to at least one resource, ordered by name.
    /// </summary>
    public IReadOnlyList<string> GetAllTags() =>
        _setting.Read(tagsByResource => GetAllTagsCore(tagsByResource).Order(StringComparer.CurrentCultureIgnoreCase).ToArray());

    /// <summary>
    /// Gets a snapshot of the tags of every tagged resource, keyed by resource display name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByResource() =>
        _setting.Read(tagsByResource => tagsByResource.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value.ToArray(), StringComparers.ResourceName));

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

        return _setting.Update(tagsByResource => AddTagCore(tagsByResource, resourceName, normalizedTag));
    }

    /// <summary>
    /// Removes a tag from a resource.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    /// <param name="tag">The tag to remove.</param>
    /// <returns><see langword="true"/> when the tag was removed; otherwise, <see langword="false"/>.</returns>
    public bool RemoveTag(string resourceName, string tag)
    {
        return _setting.Update(tagsByResource =>
        {
            if (!tagsByResource.TryGetValue(resourceName, out var tags) ||
                tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            if (tags.Count == 0)
            {
                tagsByResource.Remove(resourceName);
            }
            return true;
        });
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

    private static IEnumerable<string> GetAllTagsCore(Dictionary<string, List<string>> tagsByResource) =>
        tagsByResource.Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool AddTagCore(Dictionary<string, List<string>> tagsByResource, string resourceName, string normalizedTag)
    {
        // Reusing the casing of an existing tag keeps a single group per tag in the tag list. This also applies when
        // the tag is added again to tags read from storage, which can include the tag with another casing.
        normalizedTag = GetAllTagsCore(tagsByResource).FirstOrDefault(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)) ?? normalizedTag;

        if (!tagsByResource.TryGetValue(resourceName, out var tags))
        {
            tags = [];
            tagsByResource[resourceName] = tags;
        }
        else if (tags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        tags.Add(normalizedTag);
        return true;
    }

    // Resources aren't ordered, but each resource's tags are listed in the order they were added.
    private static bool TagsEqual(Dictionary<string, List<string>> x, Dictionary<string, List<string>> y) =>
        x.Count == y.Count && x.All(pair => y.TryGetValue(pair.Key, out var tags) && pair.Value.SequenceEqual(tags, StringComparer.Ordinal));

    // Stored tags can be edited in the browser, so they go through the same rules as tags added in the dashboard.
    private static Dictionary<string, List<string>> NormalizeTags(Dictionary<string, List<string>>? stored)
    {
        var tagsByResource = new Dictionary<string, List<string>>(StringComparers.ResourceName);
        foreach (var (resourceName, tags) in stored ?? [])
        {
            if (string.IsNullOrEmpty(resourceName) || tags is null)
            {
                continue;
            }

            foreach (var tag in tags)
            {
                if (NormalizeTag(tag) is { } normalizedTag)
                {
                    AddTagCore(tagsByResource, resourceName, normalizedTag);
                }
            }
        }

        return tagsByResource;
    }
}
