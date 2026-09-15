// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Xml.Linq;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.ResourceGraph;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceGraphMapperTests
{
    private readonly IconResolver _iconResolver = new IconResolver(NullLogger<IconResolver>.Instance);

    /// <summary>
    /// Maps a whole graph and returns the entry for one resource. The parent/child structure and the health
    /// rollup are derived across all resources at once, so a single resource can't be mapped in isolation.
    /// </summary>
    private ResourceDto MapSingle(
        ResourceViewModel resource,
        Dictionary<string, ResourceViewModel> resources,
        bool showHiddenResources,
        IReadOnlyList<ResourceViewModel>? graphResources = null)
    {
        var dtos = Map(resources, showHiddenResources, graphResources);

        return Assert.Single(dtos, d => d.Name == resource.Name);
    }

    private List<ResourceDto> Map(
        Dictionary<string, ResourceViewModel> resources,
        bool showHiddenResources,
        IReadOnlyList<ResourceViewModel>? graphResources = null)
    {
        return ResourceGraphMapper.MapResources(
            graphResources ?? [.. resources.Values],
            resources,
            new TestStringLocalizer<Columns>(),
            showHiddenResources,
            _iconResolver,
            applicationName: "TestApp");
    }

    [Fact]
    public void MapResource_HasReference_Added()
    {
        // Arrange
        var resource1 = ModelTestHelpers.CreateResource("app1-abcxyc", displayName: "app1", relationships: [new RelationshipViewModel("app2", "Reference")]);
        var resource2 = ModelTestHelpers.CreateResource("app2-123456", displayName: "app2", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource1.Name] = resource1,
            [resource2.Name] = resource2,
        };

        // Act
        var dto = MapSingle(resource1, resources, showHiddenResources: false);

        // Assert
        var referencedName = Assert.Single(dto.ChildNames);
        Assert.Equal("app2-123456", referencedName);
    }

    [Fact]
    public void MapResource_HasReferenceToReplicas_MultipleAdded()
    {
        // Arrange
        var resource1 = ModelTestHelpers.CreateResource("app1-abcxyc", displayName: "app1", relationships: [new RelationshipViewModel("app2", "Reference")]);
        var resource21 = ModelTestHelpers.CreateResource("app2-123456", displayName: "app2", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resource22 = ModelTestHelpers.CreateResource("app2-654321", displayName: "app2", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource1.Name] = resource1,
            [resource21.Name] = resource21,
            [resource22.Name] = resource22,
        };

        // Act
        var dto = MapSingle(resource1, resources, showHiddenResources: false);

        // Assert
        Assert.Collection(dto.ChildNames,
            r => Assert.Equal("app2-123456", r),
            r => Assert.Equal("app2-654321", r));
    }

    [Fact]
    public void MapResource_HasSelfReference_Ignored()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource("app1-abcxyc", displayName: "app1", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource.Name] = resource,
        };

        // Act
        var dto = MapSingle(resource, resources, showHiddenResources: false);

        // Assert
        Assert.Empty(dto.ChildNames);
    }

    [Fact]
    public void MapResource_ShowHiddenResources_IncludesHiddenResources()
    {
        // Arrange
        var resource1 = ModelTestHelpers.CreateResource("app1-abcxyc", displayName: "app1", relationships: [new RelationshipViewModel("hidden-app", "Reference")]);
        var hiddenResource = ModelTestHelpers.CreateResource("hidden-app", displayName: "hidden-app", relationships: ImmutableArray<RelationshipViewModel>.Empty, hidden: true);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource1.Name] = resource1,
            [hiddenResource.Name] = hiddenResource,
        };

        // Act
        var dto = MapSingle(resource1, resources, showHiddenResources: true);

        // Assert
        Assert.Contains("hidden-app", dto.ChildNames);
    }

    [Fact]
    public void MapResource_ParameterResource_NoEndpoint()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            "api-key",
            displayName: "api-key",
            resourceType: KnownResourceTypes.Parameter,
            relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource.Name] = resource,
        };

        // Act
        var dto = MapSingle(resource, resources, showHiddenResources: false);

        // Assert
        Assert.Null(dto.EndpointUrl);
        Assert.Null(dto.EndpointText);
    }

    [Fact]
    public void MapResource_NonParameterResource_HasEndpointText()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            "app1",
            displayName: "app1",
            resourceType: KnownResourceTypes.Container,
            relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource.Name] = resource,
        };

        // Act
        var dto = MapSingle(resource, resources, showHiddenResources: false);

        // Assert - non-parameter resources should always have endpoint text (even if "No endpoints")
        Assert.NotNull(dto.EndpointText);
    }

    [Fact]
    public void MapResource_ReferenceToResourceExcludedFromGraph_Ignored()
    {
        var resource = ModelTestHelpers.CreateResource("app", displayName: "app", relationships: [new RelationshipViewModel("api-key", "Reference")]);
        var parameter = ModelTestHelpers.CreateResource(
            "api-key",
            displayName: "api-key",
            resourceType: KnownResourceTypes.Parameter,
            relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [resource.Name] = resource,
            [parameter.Name] = parameter,
        };

        var dto = MapSingle(resource, resources, showHiddenResources: false, graphResources: [resource]);

        Assert.Empty(dto.ChildNames);
    }

    [Fact]
    public void MapResources_AddsAppHostRootThatParentsEveryTopLevelResource()
    {
        // api depends on db, so only api hangs off the app host. db is reached through api.
        var api = ModelTestHelpers.CreateResource("api", displayName: "api", relationships: [new RelationshipViewModel("db", "Reference")]);
        var db = ModelTestHelpers.CreateResource("db", displayName: "db", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var standalone = ModelTestHelpers.CreateResource("worker", displayName: "worker", relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [api.Name] = api,
            [db.Name] = db,
            [standalone.Name] = standalone,
        };

        var dtos = Map(resources, showHiddenResources: false);

        var appHost = dtos[0];
        Assert.Equal(ResourceGraphMapper.AppHostEntityName, appHost.Name);
        Assert.True(appHost.IsAppHost);
        Assert.All(dtos.Skip(1), dto => Assert.False(dto.IsAppHost));
        Assert.Equal("TestApp", appHost.DisplayName);
        Assert.Collection(appHost.ChildNames,
            n => Assert.Equal("api", n),
            n => Assert.Equal("worker", n));
    }

    [Fact]
    public void MapResources_NoResources_HasNoAppHostRoot()
    {
        var dtos = Map([], showHiddenResources: false);

        Assert.Empty(dtos);
    }

    [Fact]
    public void MapResources_AppHostHealth_IsTheWorstAcrossTheGraph()
    {
        var api = ModelTestHelpers.CreateResource("api", displayName: "api", state: KnownResourceState.Running, relationships: [new RelationshipViewModel("db", "Reference")]);
        var db = ModelTestHelpers.CreateResource("db", displayName: "db", state: KnownResourceState.FailedToStart, relationships: ImmutableArray<RelationshipViewModel>.Empty);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [api.Name] = api,
            [db.Name] = db,
        };

        var dtos = Map(resources, showHiddenResources: false);

        // The failure is two levels down, so it has to roll through api before it reaches the app host.
        Assert.Equal(nameof(HealthState.Unhealthy), dtos[0].HealthState);
    }

    [Fact]
    public void MapResources_CyclicResources_AreStillReachableFromTheAppHost()
    {
        // Every resource in a cycle has a parent, so without special handling none of them would be a root
        // and they would float with no path back to the top of the graph.
        var a = ModelTestHelpers.CreateResource("a", displayName: "a", relationships: [new RelationshipViewModel("b", "Reference")]);
        var b = ModelTestHelpers.CreateResource("b", displayName: "b", relationships: [new RelationshipViewModel("a", "Reference")]);
        var resources = new Dictionary<string, ResourceViewModel>
        {
            [a.Name] = a,
            [b.Name] = b,
        };

        var dtos = Map(resources, showHiddenResources: false);

        // Only one member of the cycle is lifted to the app host; the other is reached through it.
        var rootName = Assert.Single(dtos[0].ChildNames);
        Assert.Contains(rootName, new[] { "a", "b" });
    }

    [Fact]
    public void GetIconPathData_SinglePath_ReturnsPathData()
    {
        var icon = new Icons.Filled.Size24.Box();
        var expectedPathData = XElement.Parse(icon.Content).Attribute("d")!.Value;

        var pathData = ResourceGraphMapper.GetIconPathData(icon);

        Assert.Equal(expectedPathData, pathData);
    }

    [Fact]
    public void GetIconPathData_MultiplePaths_ReturnsCombinedPathData()
    {
        var icon = new Icons.Filled.Size24.DocumentMultiple();
        var iconContent = XElement.Parse($"<svg>{icon.Content}</svg>");
        var expectedPaths = iconContent.Elements().Select(e => e.Attribute("d")!.Value).ToArray();
        Assert.Equal(3, expectedPaths.Length);

        var pathData = ResourceGraphMapper.GetIconPathData(icon);

        Assert.Equal(string.Join(' ', expectedPaths), pathData);
    }
}
