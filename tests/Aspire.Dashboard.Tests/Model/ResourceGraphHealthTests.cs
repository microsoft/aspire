// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Model.ResourceGraph;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceGraphHealthTests
{
    [Fact]
    public void BuildEdges_ReferenceRelationship_PointsFromDependentToDependency()
    {
        // api references db, so api depends on db. db is therefore api's child and its health rolls up.
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db");

        var edges = ResourceGraphHealth.BuildEdges([api, db], showHiddenResources: false);

        Assert.Equal(new ResourceGraphEdge("api", "db"), Assert.Single(edges));
    }

    [Fact]
    public void BuildEdges_WaitForRelationship_PointsFromDependentToDependency()
    {
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.WaitFor)]);
        var db = CreateResource("db");

        var edges = ResourceGraphHealth.BuildEdges([api, db], showHiddenResources: false);

        Assert.Equal(new ResourceGraphEdge("api", "db"), Assert.Single(edges));
    }

    [Fact]
    public void BuildEdges_ParentRelationship_IsFlippedSoTheParentOwnsTheChild()
    {
        // A Parent relationship is declared on the child and points at its parent, which is the opposite
        // direction to a dependency, so the edge has to be flipped.
        var database = CreateResource("catalogdb", relationships: [new RelationshipViewModel("postgres", KnownRelationshipTypes.Parent)]);
        var server = CreateResource("postgres");

        var edges = ResourceGraphHealth.BuildEdges([database, server], showHiddenResources: false);

        Assert.Equal(new ResourceGraphEdge("postgres", "catalogdb"), Assert.Single(edges));
    }

    [Fact]
    public void BuildEdges_SelfRelationship_Ignored()
    {
        var resource = CreateResource("api", relationships: [new RelationshipViewModel("api", KnownRelationshipTypes.Reference)]);

        var edges = ResourceGraphHealth.BuildEdges([resource], showHiddenResources: false);

        Assert.Empty(edges);
    }

    [Fact]
    public void BuildEdges_HiddenDependency_ExcludedUnlessShown()
    {
        var api = CreateResource("api", relationships: [new RelationshipViewModel("secret", KnownRelationshipTypes.Reference)]);
        var hidden = CreateResource("secret", hidden: true);

        Assert.Empty(ResourceGraphHealth.BuildEdges([api, hidden], showHiddenResources: false));
        Assert.Equal(
            new ResourceGraphEdge("api", "secret"),
            Assert.Single(ResourceGraphHealth.BuildEdges([api, hidden], showHiddenResources: true)));
    }

    [Theory]
    [InlineData(KnownRelationshipTypes.Reference)]
    [InlineData(KnownRelationshipTypes.Parent)]
    public void BuildEdges_HiddenSource_ExcludedUnlessShown(string relationshipType)
    {
        var visible = CreateResource("visible");
        var hidden = CreateResource("hidden", hidden: true, relationships: [new RelationshipViewModel("visible", relationshipType)]);

        Assert.Empty(ResourceGraphHealth.BuildEdges([visible, hidden], showHiddenResources: false));

        var expected = relationshipType == KnownRelationshipTypes.Parent
            ? new ResourceGraphEdge("visible", "hidden")
            : new ResourceGraphEdge("hidden", "visible");
        Assert.Equal(expected, Assert.Single(ResourceGraphHealth.BuildEdges([visible, hidden], showHiddenResources: true)));
    }

    [Fact]
    public void BuildEdges_DuplicateRelationships_ProduceASingleEdge()
    {
        // WithReference followed by WaitFor records two relationships between the same pair of resources.
        var api = CreateResource("api", relationships:
        [
            new RelationshipViewModel("db", KnownRelationshipTypes.Reference),
            new RelationshipViewModel("db", KnownRelationshipTypes.WaitFor)
        ]);
        var db = CreateResource("db");

        var edges = ResourceGraphHealth.BuildEdges([api, db], showHiddenResources: false);

        Assert.Equal(new ResourceGraphEdge("api", "db"), Assert.Single(edges));
    }

    [Fact]
    public void BuildEdges_ReplicatedDependency_ProducesAnEdgePerReplica()
    {
        var api = CreateResource("api", relationships: [new RelationshipViewModel("worker", KnownRelationshipTypes.Reference)]);
        var worker1 = CreateResource("worker-abc", displayName: "worker");
        var worker2 = CreateResource("worker-def", displayName: "worker");

        var edges = ResourceGraphHealth.BuildEdges([api, worker1, worker2], showHiddenResources: false);

        Assert.Collection(edges,
            e => Assert.Equal(new ResourceGraphEdge("api", "worker-abc"), e),
            e => Assert.Equal(new ResourceGraphEdge("api", "worker-def"), e));
    }

    [Theory]
    [InlineData(KnownResourceState.Running, null, HealthState.Healthy)]
    [InlineData(KnownResourceState.Running, HealthStatus.Degraded, HealthState.Degraded)]
    [InlineData(KnownResourceState.Running, HealthStatus.Unhealthy, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.FailedToStart, null, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.Starting, null, HealthState.Unknown)]
    public void GetOwnState_CombinesLifecycleStateAndHealthChecks(KnownResourceState state, HealthStatus? health, HealthState expected)
    {
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", state: state, reportHealthStatus: health);

        Assert.Equal(expected, ResourceGraphHealth.GetOwnState(resource));
    }

    [Fact]
    public void ComputeEffectiveStates_UnhealthyDependency_PropagatesToTheRoot()
    {
        // web -> api -> db, where only db is broken. The whole chain back to the root should show unhealthy.
        var web = CreateResource("web", relationships: [new RelationshipViewModel("api", KnownRelationshipTypes.Reference)]);
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db", state: KnownResourceState.FailedToStart);

        var states = Compute([web, api, db]);

        Assert.Equal(HealthState.Unhealthy, states["db"]);
        Assert.Equal(HealthState.Unhealthy, states["api"]);
        Assert.Equal(HealthState.Unhealthy, states["web"]);
    }

    [Fact]
    public void ComputeEffectiveStates_DegradedDependency_PropagatesAsDegraded()
    {
        var api = CreateResource("api", relationships: [new RelationshipViewModel("cache", KnownRelationshipTypes.Reference)]);
        var cache = CreateResource("cache", health: HealthStatus.Degraded);

        var states = Compute([api, cache]);

        Assert.Equal(HealthState.Degraded, states["cache"]);
        Assert.Equal(HealthState.Degraded, states["api"]);
    }

    [Fact]
    public void ComputeEffectiveStates_AllDependenciesHealthy_AggregatesToHealthy()
    {
        var api = CreateResource("api", relationships:
        [
            new RelationshipViewModel("db", KnownRelationshipTypes.Reference),
            new RelationshipViewModel("cache", KnownRelationshipTypes.Reference)
        ]);
        var db = CreateResource("db");
        var cache = CreateResource("cache");

        var states = Compute([api, db, cache]);

        Assert.Equal(HealthState.Healthy, states["api"]);
    }

    [Fact]
    public void ComputeEffectiveStates_WorstDependencyWins()
    {
        var api = CreateResource("api", relationships:
        [
            new RelationshipViewModel("db", KnownRelationshipTypes.Reference),
            new RelationshipViewModel("cache", KnownRelationshipTypes.Reference)
        ]);
        var db = CreateResource("db", state: KnownResourceState.FailedToStart);
        var cache = CreateResource("cache", health: HealthStatus.Degraded);

        var states = Compute([api, db, cache]);

        Assert.Equal(HealthState.Unhealthy, states["api"]);
    }

    [Fact]
    public void ComputeEffectiveStates_StartingDependency_DoesNotDragTheParentDown()
    {
        // Unknown is the least severe state, so a dependency that hasn't reported yet must leave a healthy
        // parent healthy rather than making the graph look broken during startup.
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db", state: KnownResourceState.Starting);

        var states = Compute([api, db]);

        Assert.Equal(HealthState.Unknown, states["db"]);
        Assert.Equal(HealthState.Healthy, states["api"]);
    }

    [Fact]
    public void ComputeEffectiveStates_UnhealthyParent_DoesNotAffectItsChild()
    {
        // Health only flows upward. A broken consumer says nothing about the thing it consumes.
        var api = CreateResource("api", state: KnownResourceState.FailedToStart, relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db");

        var states = Compute([api, db]);

        Assert.Equal(HealthState.Unhealthy, states["api"]);
        Assert.Equal(HealthState.Healthy, states["db"]);
    }

    [Fact]
    public void ComputeEffectiveStates_DatabaseUnderServer_RollsUpIntoTheServer()
    {
        var server = CreateResource("postgres");
        var database = CreateResource("catalogdb", state: KnownResourceState.FailedToStart, relationships: [new RelationshipViewModel("postgres", KnownRelationshipTypes.Parent)]);

        var states = Compute([server, database]);

        Assert.Equal(HealthState.Unhealthy, states["postgres"]);
    }

    [Fact]
    public void ComputeEffectiveStates_CyclicDependencies_DoNotRecurseForever()
    {
        var a = CreateResource("a", relationships: [new RelationshipViewModel("b", KnownRelationshipTypes.Reference)]);
        var b = CreateResource("b", state: KnownResourceState.FailedToStart, relationships: [new RelationshipViewModel("a", KnownRelationshipTypes.Reference)]);

        var states = Compute([a, b]);

        Assert.Equal(HealthState.Unhealthy, states["a"]);
        Assert.Equal(HealthState.Unhealthy, states["b"]);
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy, false, false)]
    [InlineData(HealthStatus.Unhealthy, false, true)]
    [InlineData(HealthStatus.Unhealthy, true, false)]
    [InlineData(HealthStatus.Unhealthy, true, true)]
    [InlineData(HealthStatus.Degraded, false, false)]
    [InlineData(HealthStatus.Degraded, false, true)]
    [InlineData(HealthStatus.Degraded, true, false)]
    [InlineData(HealthStatus.Degraded, true, true)]
    public void ComputeEffectiveStates_CycleInheritsExternalDependency_RegardlessOfTraversalOrder(
        HealthStatus health, bool reverseResources, bool reverseEdges)
    {
        var a = CreateResource("a");
        var b = CreateResource("b");
        var leaf = CreateResource("leaf", health: health);
        var dependent = CreateResource("dependent");
        ResourceViewModel[] resources = [a, b, leaf, dependent];
        ResourceGraphEdge[] edges = [new("a", "b"), new("b", "a"), new("a", "leaf"), new("dependent", "b")];
        if (reverseResources)
        {
            Array.Reverse(resources);
        }
        if (reverseEdges)
        {
            Array.Reverse(edges);
        }

        var states = ResourceGraphHealth.ComputeEffectiveStates(resources, [.. edges]);
        var expected = AspireHealthModelBuilder.MapHealthStatus(health);

        Assert.Equal(4, states.Count);
        Assert.All(states.Values, state => Assert.Equal(expected, state));

        var recovered = ResourceGraphHealth.ComputeEffectiveStates(
            [a, b, CreateResource("leaf"), dependent], [.. edges]);

        Assert.All(recovered.Values, state => Assert.Equal(HealthState.Healthy, state));
    }

    [Fact]
    public void ComputeEffectiveStates_LongDependencyChain_DoesNotRequireRecursion()
    {
        const int count = 5000;
        var resources = Enumerable.Range(0, count)
            .Select(i => CreateResource($"resource-{i}", health: i == count - 1 ? HealthStatus.Unhealthy : HealthStatus.Healthy))
            .ToArray();
        var edges = Enumerable.Range(0, count - 1)
            .Select(i => new ResourceGraphEdge(resources[i].Name, resources[i + 1].Name))
            .ToImmutableArray();

        var states = ResourceGraphHealth.ComputeEffectiveStates(resources, edges);

        Assert.Equal(count, states.Count);
        Assert.All(states.Values, state => Assert.Equal(HealthState.Unhealthy, state));
    }

    [Fact]
    public void ComputeEffectiveStates_DiamondDependency_ResolvesSharedLeafOnce()
    {
        // web depends on both api and worker, which both depend on db. db is reached by two paths.
        var web = CreateResource("web", relationships:
        [
            new RelationshipViewModel("api", KnownRelationshipTypes.Reference),
            new RelationshipViewModel("worker", KnownRelationshipTypes.Reference)
        ]);
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var worker = CreateResource("worker", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db", health: HealthStatus.Degraded);

        var states = Compute([web, api, worker, db]);

        Assert.Equal(HealthState.Degraded, states["api"]);
        Assert.Equal(HealthState.Degraded, states["worker"]);
        Assert.Equal(HealthState.Degraded, states["web"]);
    }

    [Fact]
    public void GetRootNames_ResourcesNothingDependsOn_AreRoots()
    {
        var api = CreateResource("api", relationships: [new RelationshipViewModel("db", KnownRelationshipTypes.Reference)]);
        var db = CreateResource("db");
        var standalone = CreateResource("worker");

        var edges = ResourceGraphHealth.BuildEdges([api, db, standalone], showHiddenResources: false);
        var roots = ResourceGraphHealth.GetRootNames([api, db, standalone], edges);

        Assert.Collection(roots,
            n => Assert.Equal("api", n),
            n => Assert.Equal("worker", n));
    }

    [Fact]
    public void GetRootNames_CycleWithNoEntryPoint_PromotesOneMemberToARoot()
    {
        // Both resources have a parent, so neither qualifies as a root on its own. Without promoting one of
        // them the pair would be unreachable from the top of the graph.
        var a = CreateResource("a", relationships: [new RelationshipViewModel("b", KnownRelationshipTypes.Reference)]);
        var b = CreateResource("b", relationships: [new RelationshipViewModel("a", KnownRelationshipTypes.Reference)]);

        var edges = ResourceGraphHealth.BuildEdges([a, b], showHiddenResources: false);
        var roots = ResourceGraphHealth.GetRootNames([a, b], edges);

        var root = Assert.Single(roots);
        Assert.Contains(root, new[] { "a", "b" });
    }

    [Fact]
    public void GetRootNames_CycleHangingOffARealRoot_DoesNotPromoteCycleMembers()
    {
        var entry = CreateResource("entry", relationships: [new RelationshipViewModel("a", KnownRelationshipTypes.Reference)]);
        var a = CreateResource("a", relationships: [new RelationshipViewModel("b", KnownRelationshipTypes.Reference)]);
        var b = CreateResource("b", relationships: [new RelationshipViewModel("a", KnownRelationshipTypes.Reference)]);

        var edges = ResourceGraphHealth.BuildEdges([entry, a, b], showHiddenResources: false);
        var roots = ResourceGraphHealth.GetRootNames([entry, a, b], edges);

        Assert.Equal("entry", Assert.Single(roots));
    }

    private static Dictionary<string, HealthState> Compute(IReadOnlyList<ResourceViewModel> resources)
    {
        var edges = ResourceGraphHealth.BuildEdges(resources, showHiddenResources: false);
        return ResourceGraphHealth.ComputeEffectiveStates(resources, edges);
    }

    private static ResourceViewModel CreateResource(
        string name,
        string? displayName = null,
        KnownResourceState state = KnownResourceState.Running,
        HealthStatus? health = null,
        bool hidden = false,
        ImmutableArray<RelationshipViewModel>? relationships = null)
    {
        return ModelTestHelpers.CreateResource(
            resourceName: name,
            displayName: displayName ?? name,
            state: state,
            reportHealthStatus: health,
            hidden: hidden,
            relationships: relationships ?? []);
    }
}
