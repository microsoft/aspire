// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Humanizer;

namespace Aspire.Dashboard.Model;

internal sealed class ResourceFilter
{
    public string TextFilter { get; set; } = "";
    public bool ShowHiddenResources { get; set; }
    public ConcurrentDictionary<string, bool> ResourceTypesToVisibility { get; } = new(StringComparers.ResourceName);
    public ConcurrentDictionary<string, bool> ResourceStatesToVisibility { get; } = new(StringComparers.ResourceState);
    public ConcurrentDictionary<string, bool> ResourceHealthStatusesToVisibility { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, bool> ResourceTagsToVisibility { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceTagsByResource { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparers.ResourceName);

    public bool Matches(ResourceViewModel resource) => Matches(resource, TextFilter);

    // Grouped views pass an empty text filter when they also need to match against the group name.
    public bool Matches(ResourceViewModel resource, string textFilter, bool ignoreResourceType = false)
    {
        return (ignoreResourceType || IsKeyValueTrue(resource.ResourceType, ResourceTypesToVisibility))
               && IsKeyValueTrue(resource.State ?? string.Empty, ResourceStatesToVisibility)
               && IsKeyValueTrue(resource.HealthStatus?.Humanize() ?? string.Empty, ResourceHealthStatusesToVisibility)
               && IsResourceTagVisible(resource)
               && (textFilter.Length == 0 || resource.MatchesFilter(textFilter))
               && !resource.IsResourceHidden(ShowHiddenResources);
    }

    private bool IsResourceTagVisible(ResourceViewModel resource)
    {
        if (ResourceTagsToVisibility.IsEmpty)
        {
            return true;
        }

        if (!ResourceTagsByResource.TryGetValue(resource.DisplayName, out var tags) || tags.Count == 0)
        {
            return IsKeyValueTrue(string.Empty, ResourceTagsToVisibility);
        }

        return tags.Any(tag => IsKeyValueTrue(tag, ResourceTagsToVisibility));
    }

    private static bool IsKeyValueTrue(string key, IDictionary<string, bool> dictionary) =>
        dictionary.TryGetValue(key, out var value) && value;
}
