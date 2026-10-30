// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

internal static class ResourceTraitSnapshotProperties
{
    public static ImmutableArray<ResourcePropertySnapshot> AddTraitProperties(IResource resource, ImmutableArray<ResourcePropertySnapshot> properties)
    {
        var traits = resource.Annotations.OfType<TraitAnnotation>().ToArray();
        if (traits.Length == 0)
        {
            return properties;
        }

        // Each trait uses a list-valued property, e.g. resource.trait.Owner = ["Finance", "Platform"].
        // Using the existing property contract preserves traits in recordings and older tooling without
        // introducing a parallel resource protocol or flattening delimiters into user-provided values.
        var result = properties.Where(p => !p.Name.StartsWith(KnownProperties.Resource.TraitPrefix, StringComparison.Ordinal)).ToImmutableArray();
        foreach (var group in traits.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            var values = group.Key == KnownResourceTraits.DashboardPosition
                ? [group.Last().Value]
                : group.Select(t => t.Value).Distinct(StringComparer.Ordinal).ToArray();
            result = result.Add(new ResourcePropertySnapshot(KnownProperties.Resource.TraitPrefix + group.Key, values)
            {
                DisplayName = group.Key
            });
        }

        return result;
    }
}
