// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model.HealthModel;

namespace Aspire.Dashboard.Model.ResourceGraph;

/// <summary>
/// A directed parent-to-child edge in the resource graph, derived from an app host dependency.
/// </summary>
/// <param name="ParentName">The name of the resource that depends on <paramref name="ChildName"/>.</param>
/// <param name="ChildName">The name of the resource being depended on.</param>
public readonly record struct ResourceGraphEdge(string ParentName, string ChildName);

/// <summary>
/// Derives the parent-child structure of the resource graph from app host dependencies and rolls child
/// health up through it.
/// </summary>
/// <remarks>
/// <para>
/// The rollup deliberately reuses <see cref="HealthState"/> and its worst-of semantics so the graph and the
/// health model agree about what a resource's state means. In particular <see cref="HealthState.Unknown"/>
/// is the least severe state, so a dependency that has not reported yet never drags its dependents down.
/// </para>
/// <para>
/// Edges point from the resource that depends on something to the thing it depends on, so health flows from
/// leaves toward the roots of the graph: if a database is unhealthy then the service that references it is
/// shown as unhealthy too, and so on up the chain.
/// </para>
/// </remarks>
public static class ResourceGraphHealth
{
    /// <summary>
    /// Builds the parent-to-child edges between the supplied resources.
    /// </summary>
    /// <param name="resources">The resources currently displayed in the graph.</param>
    /// <param name="showHiddenResources">Whether hidden resources are being displayed.</param>
    public static ImmutableArray<ResourceGraphEdge> BuildEdges(IEnumerable<ResourceViewModel> resources, bool showHiddenResources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var graphResources = resources.Where(r => !r.IsResourceHidden(showHiddenResources)).ToList();
        var edges = ImmutableArray.CreateBuilder<ResourceGraphEdge>();
        var seen = new HashSet<ResourceGraphEdge>();

        foreach (var resource in graphResources)
        {
            foreach (var childName in GetChildNames(resource, graphResources, showHiddenResources))
            {
                var edge = new ResourceGraphEdge(resource.Name, childName);
                if (seen.Add(edge))
                {
                    edges.Add(edge);
                }
            }

            foreach (var parentName in GetParentNames(resource, graphResources, showHiddenResources))
            {
                var edge = new ResourceGraphEdge(parentName, resource.Name);
                if (seen.Add(edge))
                {
                    edges.Add(edge);
                }
            }
        }

        return edges.ToImmutable();
    }

    /// <summary>
    /// Gets the names of the resources that <paramref name="resource"/> depends on, which become its
    /// children in the graph.
    /// </summary>
    /// <remarks>
    /// A <c>WaitFor</c> or <c>Reference</c> relationship is declared on the <em>dependent</em> resource and
    /// points at the thing it needs, so the relationship target is already the child.
    /// </remarks>
    public static ImmutableArray<string> GetChildNames(ResourceViewModel resource, IEnumerable<ResourceViewModel> graphResources, bool showHiddenResources)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(graphResources);

        return ResolveRelationships(
            resource,
            graphResources,
            showHiddenResources,
            static type => !string.Equals(type, KnownRelationshipTypes.Parent, StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets the names of the resources that <paramref name="resource"/> hangs off, which become its parents
    /// in the graph.
    /// </summary>
    /// <remarks>
    /// A <c>Parent</c> relationship is declared on the <em>child</em> and points at its parent, which is the
    /// opposite direction to the dependency relationships, so these edges are flipped when they are built.
    /// A database resource pointing at its server is the common case: the database is the child, so its
    /// health rolls up into the server.
    /// </remarks>
    public static ImmutableArray<string> GetParentNames(ResourceViewModel resource, IEnumerable<ResourceViewModel> graphResources, bool showHiddenResources)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(graphResources);

        return ResolveRelationships(
            resource,
            graphResources,
            showHiddenResources,
            static type => string.Equals(type, KnownRelationshipTypes.Parent, StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets the resources that should hang directly off the synthetic app host root.
    /// </summary>
    /// <remarks>
    /// A resource is a root when nothing depends on it. Resources that only appear inside a dependency cycle
    /// have a parent but are unreachable from any root, so they are attached to the app host too rather than
    /// being left floating with no path back to the top of the graph.
    /// </remarks>
    public static ImmutableArray<string> GetRootNames(IEnumerable<ResourceViewModel> resources, ImmutableArray<ResourceGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var allNames = resources.Select(r => r.Name).ToList();
        var hasParent = edges.Select(e => e.ChildName).ToHashSet(StringComparers.ResourceName);

        var roots = allNames.Where(n => !hasParent.Contains(n)).ToList();

        var childrenByParent = edges
            .GroupBy(e => e.ParentName, StringComparers.ResourceName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ChildName).ToArray(), StringComparers.ResourceName);

        var reachable = new HashSet<string>(StringComparers.ResourceName);
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }

        foreach (var name in allNames)
        {
            if (!reachable.Contains(name))
            {
                roots.Add(name);

                // Mark everything below the newly promoted resource as reachable so only one member of a
                // cycle is lifted to the app host instead of every member of it.
                var cyclePending = new Stack<string>([name]);
                while (cyclePending.Count > 0)
                {
                    var current = cyclePending.Pop();
                    if (!reachable.Add(current))
                    {
                        continue;
                    }

                    if (childrenByParent.TryGetValue(current, out var children))
                    {
                        foreach (var child in children)
                        {
                            cyclePending.Push(child);
                        }
                    }
                }
            }
        }

        return [.. roots.OrderBy(n => n, StringComparers.ResourceName)];
    }

    /// <summary>
    /// Computes the health state of every resource after child health has been rolled up into its parents.
    /// </summary>
    /// <param name="resources">The resources currently displayed in the graph.</param>
    /// <param name="edges">The parent-to-child edges between those resources.</param>
    /// <returns>A map of resource name to the state that should be displayed for it.</returns>
    public static Dictionary<string, HealthState> ComputeEffectiveStates(IEnumerable<ResourceViewModel> resources, ImmutableArray<ResourceGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var effectiveStates = new Dictionary<string, HealthState>(StringComparers.ResourceName);
        foreach (var resource in resources)
        {
            effectiveStates[resource.Name] = GetOwnState(resource);
        }

        var parentsByChild = edges
            .Where(e => effectiveStates.ContainsKey(e.ParentName) && effectiveStates.ContainsKey(e.ChildName))
            .ToLookup(e => e.ChildName, e => e.ParentName, StringComparers.ResourceName);

        // Propagate changes back to dependents until stable. Memoizing a recursive walk can cache a
        // partially evaluated cycle (a -> b -> a, with a -> unhealthy-leaf), leaving b falsely healthy.
        // States only increase in severity within this snapshot, so even cycles converge in at most
        // three changes per resource. Starting from own state on each call also allows recovery.
        var pending = new Queue<string>(effectiveStates.Keys);
        var queued = new HashSet<string>(effectiveStates.Keys, StringComparers.ResourceName);

        while (pending.TryDequeue(out var child))
        {
            queued.Remove(child);
            foreach (var parent in parentsByChild[child])
            {
                var state = HealthStateExtensions.WorstOf(effectiveStates[parent], effectiveStates[child]);
                if (state != effectiveStates[parent])
                {
                    effectiveStates[parent] = state;
                    if (queued.Add(parent))
                    {
                        pending.Enqueue(parent);
                    }
                }
            }
        }

        return effectiveStates;
    }

    /// <summary>
    /// Gets the health state of a single resource, ignoring anything it depends on.
    /// </summary>
    internal static HealthState GetOwnState(ResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // HealthStatus is only populated while a resource is running, so fall back to the lifecycle state to
        // catch resources that failed to start or exited.
        var stateFromLifecycle = AspireHealthModelBuilder.MapResourceState(resource.KnownState);

        return resource.HealthStatus is { } healthStatus
            ? HealthStateExtensions.WorstOf(stateFromLifecycle, AspireHealthModelBuilder.MapHealthStatus(healthStatus))
            : stateFromLifecycle;
    }

    private static ImmutableArray<string> ResolveRelationships(
        ResourceViewModel resource,
        IEnumerable<ResourceViewModel> graphResources,
        bool showHiddenResources,
        Func<string, bool> includeType)
    {
        // Relationships back to the resource itself are dropped. The graph doesn't display self referential
        // edges, and treating one as a dependency would make the resource its own child.
        var relationships = resource.Relationships
            .Where(relationship => includeType(relationship.Type))
            .Where(relationship => !string.Equals(relationship.ResourceName, resource.DisplayName, StringComparisons.ResourceName));

        var resolved = new List<string>();

        foreach (var group in relationships.GroupBy(r => r.ResourceName, StringComparers.ResourceName))
        {
            // Relationships reference a resource by display name, which resolves to several resources when
            // the target is replicated. Every replica becomes an edge.
            var matches = graphResources
                .Where(r => string.Equals(r.DisplayName, group.Key, StringComparisons.ResourceName))
                .Where(r => !r.IsResourceHidden(showHiddenResources))
                .Where(r => !string.Equals(r.Name, resource.Name, StringComparisons.ResourceName));

            foreach (var match in matches)
            {
                resolved.Add(match.Name);
            }
        }

        return [.. resolved.Distinct(StringComparers.ResourceName).OrderBy(n => n, StringComparers.ResourceName)];
    }
}
