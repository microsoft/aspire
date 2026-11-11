// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class LaunchGroupInformationViewModelTests
{
    [Fact]
    public void Create_ProjectsApplicationAndResourceMetadata()
    {
        var information = new LaunchGroupInformation
        {
            SelectedGroup = "Web"
        };
        information.Groups.AddRange(["Web", "Data", "Empty", "web"]);
        information.Resources.Add(CreateResourceInformation(
            name: "api",
            groups: ["Web", "Shared", "shared"],
            inheritedFrom: "parent",
            explicitStart: true,
            excludedByGroup: false,
            excludedFromSelection: true,
            buildDisposition: LaunchGroupBuildDisposition.InitialShared));

        var model = LaunchGroupInformationViewModel.Create(information);

        Assert.Equal("Web", model.SelectedGroup);
        Assert.Equal(["Web", "Data", "Empty"], model.Groups);

        var resource = Assert.IsType<ResourceLaunchGroupViewModel>(model.FindResource("API"));
        Assert.Equal("Web", resource.SelectedGroup);
        Assert.Equal(["Web", "Shared"], resource.Groups);
        Assert.Equal("parent", resource.InheritedFrom);
        Assert.True(resource.ExplicitStart);
        Assert.False(resource.ExcludedByGroup);
        Assert.True(resource.ExcludedFromSelection);
        Assert.Equal(LaunchGroupBuildDisposition.InitialShared, resource.BuildDisposition);
    }

    [Fact]
    public void ApplyToResource_UsesDisplayNameAndPreservesReplicas()
    {
        var information = new LaunchGroupInformation();
        information.Groups.Add("Web");
        information.Resources.Add(CreateResourceInformation(name: "api", groups: ["Web"]));
        var model = LaunchGroupInformationViewModel.Create(information);
        var replica1 = ModelTestHelpers.CreateResource(resourceName: "api-abc", displayName: "api");
        var replica2 = ModelTestHelpers.CreateResource(resourceName: "api-def", displayName: "API");
        var unknown = ModelTestHelpers.CreateResource(resourceName: "worker", displayName: "worker");

        model.ApplyToResource(replica1);
        model.ApplyToResource(replica2);
        model.ApplyToResource(unknown);

        Assert.NotSame(replica1, replica2);
        Assert.Equal("api-abc", replica1.Name);
        Assert.Equal("api-def", replica2.Name);
        Assert.Same(replica1.LaunchGroup, replica2.LaunchGroup);
        Assert.Null(unknown.LaunchGroup);
    }

    [Fact]
    public void CreateVisibility_AndIsVisible_UseEffectiveMembership()
    {
        var information = new LaunchGroupInformation();
        information.Groups.AddRange(["Web", "Data", "Empty"]);
        information.Resources.Add(CreateResourceInformation(name: "api", groups: ["Web", "Data"]));
        information.Resources.Add(CreateResourceInformation(name: "worker", groups: []));
        var model = LaunchGroupInformationViewModel.Create(information);
        var api = ModelTestHelpers.CreateResource(resourceName: "api");
        var worker = ModelTestHelpers.CreateResource(resourceName: "worker");
        var unknown = ModelTestHelpers.CreateResource(resourceName: "unknown");
        model.ApplyToResource(api);
        model.ApplyToResource(worker);

        var visibility = model.CreateVisibility();

        Assert.Equal(["", "Data", "Empty", "Web"], visibility.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(LaunchGroupInformationViewModel.IsVisible(api, visibility));
        visibility["Web"] = false;
        Assert.True(LaunchGroupInformationViewModel.IsVisible(api, visibility));
        visibility["Data"] = false;
        Assert.False(LaunchGroupInformationViewModel.IsVisible(api, visibility));
        visibility[string.Empty] = false;
        Assert.False(LaunchGroupInformationViewModel.IsVisible(worker, visibility));
        Assert.True(LaunchGroupInformationViewModel.IsVisible(unknown, visibility));
    }

    [Fact]
    public void DashboardClient_DefaultLaunchGroups_IsNull()
    {
        IDashboardClient client = new TestDashboardClient();

        Assert.Null(client.LaunchGroups);
    }

    [Fact]
    public void Create_SupportedEmptyMetadataHasNoFiltersOrResourceMembership()
    {
        var model = LaunchGroupInformationViewModel.Create(new LaunchGroupInformation());
        var resource = ModelTestHelpers.CreateResource("api");

        model.ApplyToResource(resource);

        Assert.Null(model.SelectedGroup);
        Assert.Empty(model.Groups);
        Assert.Empty(model.Resources);
        Assert.Empty(model.CreateVisibility());
        Assert.Null(resource.LaunchGroup);
    }

    private static LaunchGroupResourceInfo CreateResourceInformation(
        string name,
        IReadOnlyList<string> groups,
        string? inheritedFrom = null,
        bool explicitStart = false,
        bool excludedByGroup = false,
        bool excludedFromSelection = false,
        LaunchGroupBuildDisposition buildDisposition = LaunchGroupBuildDisposition.NotCoordinated)
    {
        var information = new LaunchGroupResourceInfo
        {
            Name = name,
            ExplicitStart = explicitStart,
            ExcludedByGroup = excludedByGroup,
            ExcludedFromSelection = excludedFromSelection,
            BuildDisposition = buildDisposition
        };
        information.Groups.AddRange(groups);
        if (inheritedFrom is not null)
        {
            information.InheritedFrom = inheritedFrom;
        }

        return information;
    }
}
