// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

internal static class ResourceTraitSnapshotProperties
{
    public static ImmutableArray<ResourcePropertySnapshot> AddTraitProperties(IResource resource, ImmutableArray<ResourcePropertySnapshot> properties)
    {
        var traits = resource.Annotations.OfType<TraitAnnotation>().ToArray();
        var hasDisplayOrder = resource.TryGetLastAnnotation<DisplayOrderAnnotation>(out var displayOrder);
        if (traits.Length == 0 && !hasDisplayOrder)
        {
            return properties;
        }

        // Each trait uses a list-valued property, e.g. resource.trait.Owner = ["Finance", "Platform"]. Properties are
        // already stored in recordings and returned to tooling, so traits don't need their own protocol fields or storage,
        // and a list keeps delimiters in user-provided values from being ambiguous.
        var result = properties.Where(p => !p.Name.StartsWith(KnownProperties.Resource.TraitPrefix, StringComparison.Ordinal) &&
            p.Name != KnownProperties.Resource.DisplayOrder).ToImmutableArray();
        foreach (var group in traits.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            var values = group.Select(t => t.Value).Distinct(StringComparer.Ordinal).ToArray();
            result = result.Add(new ResourcePropertySnapshot(KnownProperties.Resource.TraitPrefix + group.Key, values)
            {
                DisplayName = group.Key
            });
        }

        if (hasDisplayOrder)
        {
            result = result.Add(new ResourcePropertySnapshot(KnownProperties.Resource.DisplayOrder, displayOrder!.Order.ToString(CultureInfo.InvariantCulture)));
        }

        return result;
    }
}
