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
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = ""
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Parameter] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void ParametersView_ShowsOnlyParameters()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Parameters,
            TextFilter = ""
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var containerResource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);
        var parameterResource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        Assert.False(vm.Filter(containerResource));
        Assert.True(vm.Filter(parameterResource));
    }

    [Fact]
    public void TypeNotVisible_ExcludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = ""
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = false;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void StateNotVisible_ExcludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = ""
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = false;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void TextFilterDoesNotMatch_ExcludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = "xyz-no-match"
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceName: "my-app", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void TextFilterMatches_IncludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = "my-app"
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceName: "my-app", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.True(vm.Filter(resource));
    }

    [Fact]
    public void HiddenResource_ExcludedWhenShowHiddenIsFalse()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = "",
            ShowHiddenResources = false
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running, hidden: true);

        Assert.False(vm.Filter(resource));
    }

    [Fact]
    public void HiddenResource_IncludedWhenShowHiddenIsTrue()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = "",
            ShowHiddenResources = true
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running, hidden: true);

        Assert.True(vm.Filter(resource));
    }

    [Fact]
    public void AllFiltersPass_IncludesResource()
    {
        var vm = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table,
            TextFilter = ""
        };
        vm.ResourceTypesToVisibility[KnownResourceTypes.Container] = true;
        vm.ResourceStatesToVisibility[KnownResourceState.Running.ToString()] = true;
        vm.ResourceHealthStatusesToVisibility["Healthy"] = true;

        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        Assert.True(vm.Filter(resource));
    }
}
