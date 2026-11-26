// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;
using static Aspire.Dashboard.Components.Pages.Resources;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourcesViewModelTests
{
    [Fact]
    public void TableView_ExcludesParameters()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Table };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Parameter] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void GraphView_ExcludesParameters()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Graph };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Parameter] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void ParametersView_ShowsOnlyParameters()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Parameters };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var containerResource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);
        var parameterResource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        Assert.False(vm.Filter(containerResource));
        Assert.True(vm.Filter(parameterResource));
    }

    [Fact]
    public void TypeNotVisible_ExcludesResource()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Table };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = false;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void StateNotVisible_ExcludesResource()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Table };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = false;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void TextFilterDoesNotMatch_ExcludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            Filters = { TextFilter = "xyz-no-match" }
        };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceName: "my-app", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void TextFilterMatches_IncludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            Filters = { TextFilter = "my-app" }
        };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceName: "my-app", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.True(vm.Filter(resource));
    }

    [Fact]
    public void HiddenResource_ExcludedWhenShowHiddenIsFalse()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            Filters = { ShowHiddenResources = false }
        };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running, hidden: true);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void HiddenResource_IncludedWhenShowHiddenIsTrue()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            Filters = { ShowHiddenResources = true }
        };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running, hidden: true);

        Assert.True(vm.Filter(resource));
    }

    [Fact]
    public void HiddenParameter_IsIncludedInParametersViewWhenShowHiddenIsTrue()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Parameters,
            Filters = { ShowHiddenResources = true }
        };
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var parameter = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running, hidden: true);

        Assert.True(vm.Filter(parameter));
    }

    [Fact]
    public void AllFiltersPass_IncludesResource()
    {
        var vm = new ResourcesViewModel { SelectedViewKind = ResourceViewKind.Table };
        vm.Filters.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.Filters.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.Filters.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.True(vm.Filter(resource));
    }
}
