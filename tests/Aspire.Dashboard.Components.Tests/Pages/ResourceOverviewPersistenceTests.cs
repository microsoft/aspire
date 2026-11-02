// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewPersistenceTests : DashboardTestContext
{
    [Theory]
    [InlineData("Persistent", KnownResourceState.Running, "Running", true)]
    [InlineData("Persistent", KnownResourceState.Exited, "Exited", true)]
    [InlineData("Session", KnownResourceState.Running, "Running", false)]
    [InlineData("Session", KnownResourceState.Exited, "Exited", false)]
    [InlineData(null, KnownResourceState.Running, "Running", false)]
    [InlineData(null, KnownResourceState.Exited, "Exited", false)]
    [InlineData("Unknown", KnownResourceState.Running, "Running", false)]
    public async Task Status_PersistentLifetimeIsSeparateFromCurrentState(string? lifetime, KnownResourceState state, string expectedState, bool persistent)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [CreateContainer(lifetime, state)], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/cache");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "cache");
            builder.CloseComponent();
        }));

        void AssertStatus(string stateText, bool hasPersistentLifetime)
        {
            var status = cut.Find("[aria-labelledby='overview-status-title']");
            var values = status.QuerySelectorAll("dt").ToDictionary(
                dt => dt.TextContent.Trim(), dt => dt.NextElementSibling!);
            Assert.Equal(stateText, values["State"].TextContent.Trim());
            Assert.Equal("Container", values["Type"].TextContent.Trim());
            Assert.Equal(hasPersistentLifetime, values.ContainsKey("Container lifetime"));
            var row = cut.Find("[data-resource-name='cache']");
            var expectedType = hasPersistentLifetime ? "Container (persistent)" : "Container";
            Assert.Equal(expectedType, row.QuerySelector(".resource-row-type")!.TextContent);
            Assert.All(cut.FindAll(".resource-status-type"), type => Assert.Equal(expectedType, type.TextContent));
            Assert.Equal($"{stateText}; {expectedType}", row.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
            Assert.Equal(hasPersistentLifetime ? 1 : 0, row.QuerySelectorAll(".resource-row-icon .persistent-container-badge").Length);
            Assert.Equal(hasPersistentLifetime ? 1 : 0, cut.FindAll(".resource-header-icon .persistent-container-badge").Count);
            Assert.Empty(cut.FindAll(".persistent-container-icon"));
            if (hasPersistentLifetime)
            {
                var lifetimeValue = values["Container lifetime"];
                Assert.Equal("Persistent", lifetimeValue.TextContent);
                const string description = "Not automatically stopped when the AppHost stops. This lifetime does not guarantee durable data.";
                Assert.Equal(description, lifetimeValue.GetAttribute("data-tooltip"));
                Assert.Equal(description, lifetimeValue.GetAttribute("aria-description"));
            }
        }

        cut.WaitForAssertion(() => AssertStatus(expectedState, persistent));

        // Metadata can change on an existing resource, without changing its selection or current state.
        await channel.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, CreateContainer(persistent ? "Session" : "Persistent", state))]);
        cut.WaitForAssertion(() => AssertStatus(expectedState, !persistent));
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
    }

    private static ResourceViewModel CreateContainer(string? lifetime, KnownResourceState state) =>
        ModelTestHelpers.CreateResource("cache", state: state, properties: lifetime is null ? [] : new()
        {
            [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString(lifetime), false, null, 0, null, false)
        });
}
