// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Orchestrator;

internal sealed class LaunchGroupService(
    IConfiguration configuration,
    IOptions<LaunchGroupsOptions> options,
    DistributedApplicationExecutionContext executionContext,
    ILogger<LaunchGroupService> logger)
{
    public bool IsActive { get; private set; }

    public void Configure(DistributedApplicationModel model)
    {
        if (executionContext.IsPublishMode)
        {
            return;
        }

        var declaredGroups = new HashSet<string>(options.Value.LaunchGroups, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection("LaunchGroups").GetChildren())
        {
            if (entry.Value is not { } name)
            {
                throw new DistributedApplicationException($"LaunchGroups configuration entry '{entry.Key}' must be a group name.");
            }

            declaredGroups.Add(LaunchGroupAnnotation.ValidateName(name, nameof(LaunchGroupsOptions.LaunchGroups)));
        }

        var memberships = model.Resources.SelectMany(resource => resource.Annotations.OfType<LaunchGroupAnnotation>()).ToArray();
        if (declaredGroups.Count > 0)
        {
            foreach (var resource in model.Resources)
            {
                foreach (var membership in resource.Annotations.OfType<LaunchGroupAnnotation>())
                {
                    if (!declaredGroups.Contains(membership.LaunchGroupName))
                    {
                        throw new DistributedApplicationException($"Resource '{resource.Name}' belongs to undefined launch group '{membership.LaunchGroupName}'.");
                    }
                }
            }
        }
        else
        {
            declaredGroups.UnionWith(memberships.Select(annotation => annotation.LaunchGroupName));
        }

        var selectedGroup = configuration["LaunchGroup"];
        if (string.IsNullOrEmpty(selectedGroup))
        {
            return;
        }

        if (!declaredGroups.Contains(selectedGroup))
        {
            throw new DistributedApplicationException(
                $"Launch group '{selectedGroup}' is invalid. Select one of: {string.Join(", ", declaredGroups.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        var roots = model.Resources.Where(resource => !resource.HasAnnotationOfType<ExplicitStartupAnnotation>()).Where(resource =>
        {
            var groups = GetMembership(resource).ToArray();
            return groups.Length == 0 || groups.Any(group => StringComparer.OrdinalIgnoreCase.Equals(group, selectedGroup));
        }).ToArray();
        var selected = GetClosure(roots).ToHashSet();
        foreach (var resource in model.Resources)
        {
            if (!selected.Contains(resource) && !resource.HasAnnotationOfType<ExplicitStartupAnnotation>())
            {
                resource.Annotations.Add(new ExplicitStartupAnnotation());
            }

            if (resource.HasAnnotationOfType<ExplicitStartupAnnotation>())
            {
                resource.Annotations.Add(LaunchGroupDeferredAnnotation.Instance);
            }
        }

        IsActive = true;
        logger.LogInformation(
            "Launch group '{LaunchGroup}' is active. Unselected resources remain available for explicit starts.",
            selectedGroup);
    }

    internal static IReadOnlyList<IResource> GetClosure(IEnumerable<IResource> roots)
    {
        var result = new List<IResource>();
        var visited = new HashSet<IResource>();
        foreach (var root in roots)
        {
            Visit(root);
        }

        return result;

        void Visit(IResource resource)
        {
            if (!visited.Add(resource))
            {
                return;
            }

            foreach (var dependency in GetDependencies(resource))
            {
                Visit(dependency);
            }

            result.Add(resource);
        }
    }

    private static IEnumerable<string> GetMembership(IResource resource)
    {
        var visited = new HashSet<IResource>();
        while (visited.Add(resource))
        {
            var annotations = resource.Annotations.OfType<LaunchGroupAnnotation>().ToArray();
            if (annotations.Length > 0)
            {
                return annotations.Select(annotation => annotation.LaunchGroupName);
            }

            if (GetParent(resource) is not { } parent)
            {
                break;
            }

            resource = parent;
        }

        return [];
    }

    private static IResource? GetParent(IResource resource) =>
        resource is IResourceWithParent child
            ? child.Parent
            : resource.Annotations.OfType<ResourceRelationshipAnnotation>().LastOrDefault(annotation => annotation.Type == KnownRelationshipTypes.Parent)?.Resource;

    private static IEnumerable<IResource> GetDependencies(IResource resource)
    {
        if (GetParent(resource) is { } parent)
        {
            yield return parent;
        }

        foreach (var wait in resource.Annotations.OfType<WaitAnnotation>())
        {
            yield return wait.Resource;
        }

        foreach (var reference in resource.Annotations.OfType<ResourceRelationshipAnnotation>()
            .Where(annotation => annotation.Type == KnownRelationshipTypes.Reference))
        {
            yield return reference.Resource;
        }

        if (resource.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var redirect))
        {
            yield return redirect.Resource;
        }

        // Builder APIs record references without evaluating runtime callbacks. Evaluating callbacks here can
        // deadlock on endpoints or coordinated build output that cannot exist until startup has begun.
        if (resource is IResourceWithConnectionString connectionString)
        {
            var references = new HashSet<IResource>();
            ResourceExtensions.VisitReferences(connectionString.ConnectionStringExpression, new HashSet<object>(ReferenceEqualityComparer.Instance), value =>
            {
                if (value is IResource reference)
                {
                    references.Add(reference);
                }
            });
            foreach (var reference in references)
            {
                yield return reference;
            }
        }
    }
}

internal sealed class LaunchGroupDeferredAnnotation : IResourceAnnotation
{
    public static LaunchGroupDeferredAnnotation Instance { get; } = new();
}
