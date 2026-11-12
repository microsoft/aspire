// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of launch-group membership annotations.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Orchestrator;

internal sealed class LaunchGroupService(
    IConfiguration configuration,
    DistributedApplicationExecutionContext executionContext,
    LaunchGroupMetadataService metadata)
{
    internal const string ConfigurationSectionName = "Aspire:LaunchGroups";

    private readonly Dictionary<IResource, ExplicitStartupAnnotation> _selectionAnnotations =
        new(ReferenceEqualityComparer.Instance);

    public void Configure(DistributedApplicationModel model)
    {
        if (executionContext.IsPublishMode)
        {
            return;
        }

        var declaredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredGroups = configuration.GetSection(ConfigurationSectionName);
        if (configuredGroups.Value is not null)
        {
            throw new DistributedApplicationException(
                $"{ConfigurationSectionName} must be an array of group names.");
        }

        foreach (var entry in configuredGroups.GetChildren())
        {
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new DistributedApplicationException(
                    $"{ConfigurationSectionName} must be an array of group names.");
            }

            if (entry.Value is not { } name)
            {
                throw new DistributedApplicationException(
                    $"{ConfigurationSectionName} configuration entry '{entry.Key}' must be a group name.");
            }

            declaredGroups.Add(LaunchGroupAnnotation.ValidateName(name, ConfigurationSectionName));
        }

        var memberships = model.Resources.SelectMany(resource => resource.Annotations.OfType<LaunchGroupAnnotation>()).ToArray();
        var hasConfiguredGroups = declaredGroups.Count > 0;
        if (hasConfiguredGroups)
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

        var selection = configuration.GetSection("Aspire:LaunchGroup");
        // Arrays and objects such as "LaunchGroup": ["catalog", "basket"] have children
        // but no scalar value. Reject children even when another provider supplies a scalar.
        if (selection.GetChildren().Any())
        {
            throw new DistributedApplicationException("Aspire:LaunchGroup must be a single group name.");
        }

        var selectedGroup = selection.Value;
        foreach (var resource in model.Resources)
        {
            foreach (var membership in GetEffectiveLaunchGroups(resource))
            {
                if (hasConfiguredGroups && !declaredGroups.Contains(membership.LaunchGroupName))
                {
                    throw new DistributedApplicationException($"Resource '{resource.Name}' belongs to undefined launch group '{membership.LaunchGroupName}'.");
                }

                if (!hasConfiguredGroups)
                {
                    declaredGroups.Add(membership.LaunchGroupName);
                }
            }
        }

        if (string.IsNullOrEmpty(selectedGroup))
        {
            metadata.ValidateBuildSelection(selectedGroup: null, HasUserExplicitStart);
            ClearSelectionAnnotations();
            metadata.CaptureSelection(model.Resources, selectedGroup: null, declaredGroups);
            return;
        }

        if (!declaredGroups.Contains(selectedGroup))
        {
            var validGroupsDescription = declaredGroups.Count == 0
                ? "No launch groups have been defined."
                : $"Select one of: {string.Join(", ", declaredGroups.Order(StringComparer.OrdinalIgnoreCase))}.";
            throw new DistributedApplicationException(
                $"Launch group '{selectedGroup}' is invalid. {validGroupsDescription}");
        }

        metadata.ValidateBuildSelection(selectedGroup, HasUserExplicitStart);
        ValidateSelectedGroupDependencies(model.Resources, selectedGroup);
        ClearSelectionAnnotations();
        // Keep user-requested explicit start distinct from the annotation applied by selection.
        metadata.CaptureSelection(model.Resources, selectedGroup, declaredGroups);

        foreach (var resource in model.Resources)
        {
            if (!resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>() &&
                !resource.HasAnnotationOfType<ExplicitStartupAnnotation>() &&
                !BelongsToLaunchGroup(resource, selectedGroup))
            {
                var annotation = new ExplicitStartupAnnotation();
                resource.Annotations.Add(annotation);
                _selectionAnnotations.Add(resource, annotation);
            }
        }
    }

    private void ClearSelectionAnnotations()
    {
        foreach (var (resource, annotation) in _selectionAnnotations)
        {
            resource.Annotations.Remove(annotation);
        }

        _selectionAnnotations.Clear();
    }

    private bool HasUserExplicitStart(IResource resource)
    {
        _selectionAnnotations.TryGetValue(resource, out var selectionAnnotation);
        return resource.Annotations.OfType<ExplicitStartupAnnotation>()
            .Any(annotation => !ReferenceEquals(annotation, selectionAnnotation));
    }

    private void ValidateSelectedGroupDependencies(IEnumerable<IResource> resources, string selectedGroup)
    {
        var resourceList = resources.ToArray();
        var automaticStartResources = new HashSet<IResource>(
            resourceList.Where(resource => !WillStartExplicitly(resource, selectedGroup)),
            ReferenceEqualityComparer.Instance);
        var roots = resourceList
            .Where(automaticStartResources.Contains)
            .Where(IsLocalExecutionResource)
            .Where(resource => resource is not IResourceWithParent || ApplicationOrchestrator.ResourceHasOwnLifetime(resource))
            .ToArray();
        var rootSet = new HashSet<IResource>(roots, ReferenceEqualityComparer.Instance);
        var referencedRoots = new HashSet<IResource>(ReferenceEqualityComparer.Instance);
        var scanned = new HashSet<IResource>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IResource>(roots);

        // Prefer entry points over resources they depend on so diagnostics retain
        // the complete path even when an intermediate resource was registered first.
        while (pending.TryPop(out var current))
        {
            if (!scanned.Add(current))
            {
                continue;
            }

            foreach (var edge in GetDependencies(current))
            {
                if (rootSet.Contains(edge.Target) && !ReferenceEquals(current, edge.Target))
                {
                    referencedRoots.Add(edge.Target);
                }

                pending.Push(edge.Target);
            }
        }

        foreach (var resource in roots.OrderBy(referencedRoots.Contains))
        {
            ValidateDependencies(
                resource,
                resource,
                selectedGroup,
                automaticStartResources,
                new HashSet<IResource>(ReferenceEqualityComparer.Instance),
                []);
        }
    }

    private void ValidateDependencies(
        IResource root,
        IResource current,
        string selectedGroup,
        HashSet<IResource> automaticStartResources,
        HashSet<IResource> visited,
        List<DependencyEdge> path)
    {
        if (!visited.Add(current))
        {
            return;
        }

        foreach (var edge in GetDependencies(current))
        {
            var dependency = edge.Target;
            var lifetimeChain = GetLifetimeChain(dependency);
            var lifetimeOwner = lifetimeChain[^1];
            path.Add(edge);
            if (IsLocalExecutionResource(lifetimeOwner) &&
                !automaticStartResources.Contains(lifetimeOwner))
            {
                for (var index = 1; index < lifetimeChain.Count; index++)
                {
                    path.Add(new DependencyEdge(lifetimeChain[index - 1], lifetimeChain[index], DependencyKind.LifetimeOwner));
                }

                var explicitStart = HasUserExplicitStart(lifetimeOwner);
                var reason = explicitStart
                    ? "is marked for explicit start"
                    : $"is not included in launch group '{selectedGroup}'";
                var dependencyDescription = ReferenceEquals(dependency, lifetimeOwner)
                    ? $"local resource '{lifetimeOwner.Name}'"
                    : $"local resource '{lifetimeOwner.Name}' (the lifetime owner of '{dependency.Name}')";
                var remedy = explicitStart
                    ? $"Remove explicit start from '{lifetimeOwner.Name}' and ensure it is included in launch group '{selectedGroup}'."
                    : $"Add '{lifetimeOwner.Name}' to launch group '{selectedGroup}'.";

                // WaitFor on a passive child can target its server. Making the child reference
                // optional does not remove a wait or redirection to that same lifetime owner.
                var waitDependency = current.Annotations.OfType<WaitAnnotation>()
                    .Where(annotation => annotation.Resource is not IResourceWithoutLifetime)
                    .Select(annotation => annotation.Resource)
                    .FirstOrDefault(resource => ReferenceEquals(GetLifetimeOwner(resource), lifetimeOwner));
                var redirectDependency = current.Annotations.OfType<ConnectionStringRedirectAnnotation>()
                    .Select(annotation => annotation.Resource)
                    .FirstOrDefault(resource => ReferenceEquals(GetLifetimeOwner(resource), lifetimeOwner));
                if (waitDependency is not null)
                {
                    remedy += $" The wait from '{current.Name}' to '{waitDependency.Name}' requires automatic startup.";
                }
                if (redirectDependency is not null)
                {
                    remedy += $" The connection string redirection from '{current.Name}' to '{redirectDependency.Name}' requires automatic startup.";
                }
                if (waitDependency is null && redirectDependency is null && edge.Kind.HasFlag(DependencyKind.Reference))
                {
                    remedy += $" If the reference from '{current.Name}' to '{dependency.Name}' is optional, mark it with " +
                        $"{nameof(LaunchGroupExtensions.WithOptionalLaunchGroupReference)}.";
                }

                var names = string.Join(" -> ", path.Select(item => item.Source.Name).Append(path[^1].Target.Name));
                var edges = string.Join("; ", path.Select(item => $"{item.Source.Name} --{DescribeDependencyKind(item.Kind)}--> {item.Target.Name}"));
                throw new DistributedApplicationException(
                    $"Resource '{root.Name}' requires {dependencyDescription}, which {reason}. " +
                    $"Dependency path: {names}. Edges: {edges}. {remedy}");
            }

            ValidateDependencies(root, dependency, selectedGroup, automaticStartResources, visited, path);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static List<DependencyEdge> GetDependencies(IResource current)
    {
        var optionalReferences = current.Annotations
            .OfType<OptionalLaunchGroupDependencyAnnotation>()
            .Select(annotation => annotation.Resource)
            .ToHashSet<IResource>(ReferenceEqualityComparer.Instance);
        var dependencies = new List<DependencyEdge>();

        void Add(IResource target, DependencyKind kind)
        {
            var index = dependencies.FindIndex(edge => ReferenceEquals(edge.Target, target));
            if (index < 0)
            {
                dependencies.Add(new DependencyEdge(current, target, kind));
            }
            else
            {
                dependencies[index] = dependencies[index] with { Kind = dependencies[index].Kind | kind };
            }
        }

        foreach (var annotation in current.Annotations.OfType<ResourceRelationshipAnnotation>())
        {
            if (annotation.Type == KnownRelationshipTypes.Reference && !optionalReferences.Contains(annotation.Resource))
            {
                Add(annotation.Resource, DependencyKind.Reference);
            }
        }

        // ResourceNotificationService ignores waits on resources without a lifetime.
        // WaitFor and WaitForStart can also record a separate wait on the parent.
        foreach (var annotation in current.Annotations.OfType<WaitAnnotation>())
        {
            if (annotation.Resource is not IResourceWithoutLifetime)
            {
                Add(annotation.Resource, DependencyKind.Wait);
            }
        }

        foreach (var annotation in current.Annotations.OfType<ConnectionStringRedirectAnnotation>())
        {
            Add(annotation.Resource, DependencyKind.Redirect);
        }

        if (!ApplicationOrchestrator.ResourceHasOwnLifetime(current) && current is IResourceWithParent child)
        {
            Add(child.Parent, DependencyKind.LifetimeOwner);
        }

        return dependencies;
    }

    private static string DescribeDependencyKind(DependencyKind kind)
    {
        var parts = new List<string>();
        if (kind.HasFlag(DependencyKind.Reference))
        {
            parts.Add("reference");
        }
        if (kind.HasFlag(DependencyKind.Wait))
        {
            parts.Add("wait");
        }
        if (kind.HasFlag(DependencyKind.Redirect))
        {
            parts.Add("redirect");
        }
        if (kind.HasFlag(DependencyKind.LifetimeOwner))
        {
            parts.Add("lifetime owner");
        }

        return string.Join("/", parts);
    }

    private static IResource GetLifetimeOwner(IResource resource) => GetLifetimeChain(resource)[^1];

    private static List<IResource> GetLifetimeChain(IResource resource)
    {
        // Only passive children share their parent's lifetime. Executable children have
        // their own lifetime even when they inherit group membership.
        var chain = new List<IResource> { resource };
        var visited = new HashSet<IResource>(ReferenceEqualityComparer.Instance) { resource };
        while (!ApplicationOrchestrator.ResourceHasOwnLifetime(resource) &&
            resource is IResourceWithParent child)
        {
            resource = child.Parent;
            if (!visited.Add(resource))
            {
                throw new DistributedApplicationException(
                    $"A circular structural parent chain prevents determining the lifetime owner of resource '{chain[0].Name}'.");
            }

            chain.Add(resource);
        }

        return chain;
    }

    // Custom and cloud resources can initialize independently of DCP, so group
    // exclusion does not necessarily prevent them from becoming available.
    internal static bool IsLocalExecutionResource(IResource resource) =>
        resource is not IResourceWithoutLifetime &&
        (resource.IsContainer() || resource is ProjectResource or ExecutableResource or ContainerExecutableResource);

    private bool WillStartExplicitly(IResource resource, string selectedGroup) =>
        HasUserExplicitStart(resource) ||
        (!resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>() &&
            !BelongsToLaunchGroup(resource, selectedGroup));

    internal static bool BelongsToLaunchGroup(IResource resource, string selectedGroup) =>
        GetEffectiveLaunchGroups(resource)
            .Any(annotation => StringComparer.OrdinalIgnoreCase.Equals(annotation.LaunchGroupName, selectedGroup));

    private static IEnumerable<LaunchGroupAnnotation> GetEffectiveLaunchGroups(IResource resource) =>
        GetEffectiveLaunchGroupSource(resource)?.Annotations.OfType<LaunchGroupAnnotation>() ?? [];

    internal static IResource? GetEffectiveLaunchGroupSource(IResource resource)
    {
        var current = resource;
        HashSet<IResource>? visited = null;
        while (true)
        {
            if (current.HasAnnotationOfType<LaunchGroupAnnotation>())
            {
                return current;
            }

            if (current is not IResourceWithParent child)
            {
                return null;
            }

            visited ??= new HashSet<IResource>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(current))
            {
                throw new DistributedApplicationException(
                    $"A circular structural parent chain prevents determining launch-group membership for resource '{resource.Name}'.");
            }

            current = child.Parent;
        }
    }

    [Flags]
    private enum DependencyKind
    {
        Reference = 1,
        Wait = 2,
        Redirect = 4,
        LifetimeOwner = 8
    }

    private readonly record struct DependencyEdge(IResource Source, IResource Target, DependencyKind Kind);
}
