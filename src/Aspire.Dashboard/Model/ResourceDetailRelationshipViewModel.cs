// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public sealed class ResourceDetailRelationshipViewModel
{
    public required ResourceViewModel Resource { get; init; }
    public required string ResourceName { get; init; }
    public required List<string> Types { get; set; }

    public bool MatchesFilter(string filter)
    {
        return Resource.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Types.Any(t => t.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
    }

    public static ResourceDetailRelationshipViewModel Create(ResourceViewModel resource, string resourceName, IEnumerable<RelationshipViewModel> matches)
    {
        return new ResourceDetailRelationshipViewModel
        {
            Resource = resource,
            ResourceName = resourceName,
            Types = matches.Select(r => r.Type).Distinct().OrderBy(r => r).ToList()
        };
    }
}
