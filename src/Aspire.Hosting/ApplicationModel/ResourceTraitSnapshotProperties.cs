// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

internal static class ResourceTraitSnapshotProperties
{
    public static ImmutableArray<ResourcePropertySnapshot> AddTraitProperties(IResource resource, ImmutableArray<ResourcePropertySnapshot> properties)
    {
        var traits = resource.Annotations.OfType<TraitAnnotation>().ToList();
#pragma warning disable ASPIRETESTING001 // Project testing participation through the existing dashboard property contract.
        var isTesting = resource.TryGetLastAnnotation<TestingAnnotation>(out _);
#pragma warning restore ASPIRETESTING001
        if (isTesting && !traits.Any(t => t.Name == KnownResourceTraits.DashboardTag && string.Equals(t.Value, "Testing", StringComparison.OrdinalIgnoreCase)))
        {
            traits.Add(new TraitAnnotation(KnownResourceTraits.DashboardTag, "Testing"));
        }
        if (traits.Count == 0 && !isTesting)
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
                : group.Select(t => t.Value).Distinct(isTesting && group.Key == KnownResourceTraits.DashboardTag ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            result = result.Add(new ResourcePropertySnapshot(KnownProperties.Resource.TraitPrefix + group.Key, values)
            {
                DisplayName = group.Key
            });
        }

        if (isTesting)
        {
            // Stamp every initial/update/replica snapshot, even during ordinary runs. A tag alone
            // is not participation: users can also tag a non-test resource "Testing".
            result = result.Where(p => p.Name != KnownProperties.Resource.Testing).ToImmutableArray()
                .Add(new ResourcePropertySnapshot(KnownProperties.Resource.Testing, "true"));
        }

        return result;
    }
}
