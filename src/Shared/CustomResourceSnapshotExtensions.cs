// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class CustomResourceSnapshotExtensions
{
    internal static ImmutableArray<ResourcePropertySnapshot> SetResourceProperty(this ImmutableArray<ResourcePropertySnapshot> properties, string name, object value, bool IsSensitive = false)
    {
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];

            if (string.Equals(property.Name, name, StringComparisons.ResourcePropertyName))
            {
                if (property.Value == value)
                {
                    // Unchanged.
                    return properties;
                }

                // Set value.
                return properties.SetItem(i, property with { Value = value, IsSensitive = IsSensitive });
            }
        }

        // Add property.
        return [.. properties, new ResourcePropertySnapshot(name, value) { IsSensitive = IsSensitive }];
    }

    internal static ImmutableArray<ResourcePropertySnapshot> SetResourcePropertyRange(this ImmutableArray<ResourcePropertySnapshot> properties, IEnumerable<ResourcePropertySnapshot> newValues)
    {
        var existingProperties = new List<ResourcePropertySnapshot>(properties);
        var propertiesToAdd = new List<ResourcePropertySnapshot>();

        foreach (var newValue in newValues)
        {
            var found = false;
            for (var i = 0; i < existingProperties.Count; i++)
            {
                var existingProperty = existingProperties[i];

                if (string.Equals(existingProperty.Name, newValue.Name, StringComparisons.ResourcePropertyName))
                {
                    if (existingProperty.Value != newValue.Value)
                    {
                        existingProperties[i] = existingProperty with { Value = newValue.Value };
                    }

                    found = true;
                    break;
                }
            }

            if (!found)
            {
                propertiesToAdd.Add(newValue);
            }
        }

        return [.. existingProperties, .. propertiesToAdd];
    }

    internal static ImmutableArray<ResourcePropertySnapshot> RemoveResourceProperty(this ImmutableArray<ResourcePropertySnapshot> properties, string name)
    {
        for (var i = 0; i < properties.Length; i++)
        {
            if (string.Equals(properties[i].Name, name, StringComparisons.ResourcePropertyName))
            {
                return properties.RemoveAt(i);
            }
        }

        return properties;
    }
}
