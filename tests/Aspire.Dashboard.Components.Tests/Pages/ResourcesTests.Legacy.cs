// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public partial class ResourcesTests
{
    [Fact]
    public async Task LegacyLaunchGroupFilters_RoundTripUrlState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resources = new List<ResourceViewModel>
        {
            ModelTestHelpers.CreateResource("api"),
            ModelTestHelpers.CreateResource("worker"),
            ModelTestHelpers.CreateResource("shared"),
            ModelTestHelpers.CreateResource("ungrouped")
        };
        var information = LaunchGroupTestHelpers.CreateInformation(
            selectedGroup: "Web",
            groups: ["Web", "Data Group"],
            resources:
            [
                LaunchGroupTestHelpers.CreateResource("api", groups: ["Web"]),
                LaunchGroupTestHelpers.CreateResource("worker", groups: ["Data Group"]),
                LaunchGroupTestHelpers.CreateResource("shared", groups: ["Web", "Data Group"]),
                LaunchGroupTestHelpers.CreateResource("ungrouped")
            ]);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            new LaunchGroupTestDashboardClient(
                information,
                resources,
                Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        SetupResourceGraphModule();
        var cut = Render<Components.Legacy.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(4, cut.Instance.GetFilteredResources().Count()));

        cut.Instance.PageViewModel.ResourceLaunchGroupsToVisibility["Web"] = false;
        Assert.Equal(
            ["shared", "ungrouped", "worker"],
            cut.Instance.GetFilteredResources().Select(resource => resource.Name).Order(StringComparer.Ordinal));

        var url = cut.Instance.GetUrlFromSerializableViewModel(cut.Instance.ConvertViewModelToSerializable());
        var hiddenLaunchGroups = QueryHelpers.ParseQuery(new Uri(new Uri("http://localhost"), url).Query)["hiddenLaunchGroups"].ToString();
        Assert.NotEmpty(hiddenLaunchGroups);
        cut.Instance.PageViewModel.ResourceLaunchGroupsToVisibility["Web"] = true;
#pragma warning disable BL0005 // Set the query parameter without a router.
        cut.Instance.HiddenLaunchGroups = hiddenLaunchGroups;
#pragma warning restore BL0005
        await cut.Instance.UpdateViewModelFromQueryAsync(cut.Instance.PageViewModel);

        Assert.False(cut.Instance.PageViewModel.ResourceLaunchGroupsToVisibility["Web"]);
        Assert.True(cut.Instance.PageViewModel.ResourceLaunchGroupsToVisibility["Data Group"]);
        Assert.Equal(
            ["shared", "ungrouped", "worker"],
            cut.Instance.GetFilteredResources().Select(resource => resource.Name).Order(StringComparer.Ordinal));
    }
}
