// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.HealthModels;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal static class HealthModelBindingValidator
{
    public static void Validate(HealthModelDocument document, IEnumerable<IResource> resources, string applicationName)
    {
        if (document.ApplicationName != applicationName)
        {
            throw new InvalidDataException("The saved health model belongs to a different application. Export the definition from this AppHost.");
        }

        var byName = resources.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var bindings = document.Entities.Where(e => e.AspireResourceName is not null)
            .ToLookup(e => e.AspireResourceName!, StringComparer.OrdinalIgnoreCase);
        var expectedEdges = new HashSet<HealthModelRelationship>();

        foreach (var group in bindings)
        {
            if (!byName.TryGetValue(group.Key, out var resource))
            {
                throw new InvalidDataException($"The saved health model references resource '{group.Key}', which is not in the AppHost.");
            }

            var checks = resource.Annotations.OfType<HealthCheckAnnotation>().Select(a => a.Key)
                .Append("resource-state").ToHashSet(StringComparer.Ordinal);
            foreach (var entity in group)
            {
                if (!checks.SetEquals(entity.LocalSignals.Select(s => s.Name)))
                {
                    throw new InvalidDataException($"The saved health signals on resource '{group.Key}' differ from the AppHost checks. Re-export the model after adding, removing or renaming checks.");
                }
            }

            foreach (var relationship in resource.Annotations.OfType<ResourceRelationshipAnnotation>())
            {
                AddEdges(group, bindings[relationship.Resource.Name], relationship.Type == KnownRelationshipTypes.Parent);
            }
            if (resource is IResourceWithParent child)
            {
                AddEdges(group, bindings[child.Parent.Name], reversed: true);
            }
        }

        var children = expectedEdges.Select(e => e.ChildEntityName).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in document.Entities.Where(e => e.Name != document.Name && !children.Contains(e.Name)))
        {
            expectedEdges.Add(new(document.Name, entity.Name));
        }
        if (!expectedEdges.SetEquals(document.Relationships))
        {
            throw new InvalidDataException("The saved health model topology differs from the AppHost dependencies. Re-export the model rather than publishing stale relationships.");
        }

        void AddEdges(IEnumerable<HealthModelEntityConfiguration> sources, IEnumerable<HealthModelEntityConfiguration> targets, bool reversed)
        {
            foreach (var source in sources)
            {
                foreach (var target in targets)
                {
                    if (source.Name != target.Name)
                    {
                        expectedEdges.Add(reversed ? new(target.Name, source.Name) : new(source.Name, target.Name));
                    }
                }
            }
        }
    }
}
