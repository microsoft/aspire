// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public partial class ResourcesTests
{
    [Theory]
    [InlineData(KnownResourceState.Starting)]
    [InlineData(KnownResourceState.Waiting)]
    public async Task LegacyResourceDetails_ParameterOnlyUpdates_RefreshMissingParameterDescription(KnownResourceState state)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var firstParameter = ModelTestHelpers.CreateResource(
            resourceName: "first-selector", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var secondParameter = ModelTestHelpers.CreateResource(
            resourceName: "second-selector", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "api", state: state,
            relationships: [new(firstParameter.Name, KnownRelationshipTypes.Reference), new(secondParameter.Name, KnownRelationshipTypes.Reference)]);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true, initialResources: [resource, firstParameter, secondParameter],
            resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        var cut = Render<Components.Legacy.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => cut.Instance.PageViewModel.SelectedResource = resource);
        cut.Render();

        var stateDescription = state is KnownResourceState.Waiting ? Columns.StateColumnResourceWaiting : "Starting";
        AssertDescription("first-selector, second-selector");

        foreach (var parameter in new[] { firstParameter, secondParameter })
        {
            await channel.Writer.WriteAsync([new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                ModelTestHelpers.CreateResource(
                    resourceName: parameter.Name, resourceType: KnownResourceTypes.Parameter,
                    state: KnownResourceState.Running))]);

            cut.WaitForAssertion(() =>
            {
                Assert.Same(resource, cut.FindComponent<ResourceDetails>().Instance.Resource);
                AssertDescription(parameter == firstParameter ? "second-selector" : null);
            });
        }

        void AssertDescription(string? missingParameters)
        {
            var description = missingParameters is not null
                ? $"{stateDescription} {string.Format(CultureInfo.InvariantCulture, Columns.StateColumnResourceMissingParameters, missingParameters)}"
                : stateDescription;
            var expectedDescriptions = missingParameters is not null || state is KnownResourceState.Waiting ? new[] { description } : [];
            Assert.Equal(expectedDescriptions, cut.FindComponent<ResourceDetails>().Instance.FilteredResourceProperties
                .Where(property => property.KnownProperty?.Key == "resource-state-description")
                .Select(property => property.Value.StringValue));
        }
    }
}
