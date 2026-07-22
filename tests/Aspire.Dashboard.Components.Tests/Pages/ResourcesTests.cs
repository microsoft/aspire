// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using TelemetryTestHelpers = Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class ResourcesTests : DashboardTestContext
{
    [Fact]
    public void UpdateResources_FiltersUpdated()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource(
                "Resource1",
                "Type1",
                "Running",
                ImmutableArray.Create(new HealthReportViewModel("Null", null, "Description1", null))),
        };
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Assert 1
        Assert.Collection(cut.Instance.PageViewModel.ResourceTypesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Type1", kvp.Key);
                Assert.True(kvp.Value);
            });
        Assert.Collection(cut.Instance.PageViewModel.ResourceStatesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Running", kvp.Key);
                Assert.True(kvp.Value);
            });
        Assert.Collection(cut.Instance.PageViewModel.ResourceHealthStatusesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Unhealthy", kvp.Key);
                Assert.True(kvp.Value);
            });

        // Act
        channel.Writer.TryWrite([
            new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                CreateResource(
                    "Resource2",
                    "Type2",
                    "Running",
                    ImmutableArray.Create(new HealthReportViewModel("Healthy", HealthStatus.Healthy, "Description2", null))))
            ]);

        cut.WaitForState(() => cut.Instance.GetFilteredResources().Count() == 2);

        // Assert 2
        Assert.Collection(cut.Instance.PageViewModel.ResourceTypesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Type1", kvp.Key);
                Assert.True(kvp.Value);
            },
            kvp =>
            {
                Assert.Equal("Type2", kvp.Key);
                Assert.True(kvp.Value);
            });
        Assert.Collection(cut.Instance.PageViewModel.ResourceStatesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Running", kvp.Key);
                Assert.True(kvp.Value);
            });
        Assert.Collection(cut.Instance.PageViewModel.ResourceHealthStatusesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Healthy", kvp.Key);
                Assert.True(kvp.Value);
            },
            kvp =>
            {
                Assert.Equal("Unhealthy", kvp.Key);
                Assert.True(kvp.Value);
            });
    }

    [Fact]
    public void UpdateResources_ChildResourceRunning_UpdatesParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var child = CreateReplicaChild(parent, "syndule-api--0000007", "Scaled to zero");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, child], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        channel.Writer.TryWrite([
            new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                CreateReplicaChild(parent, "syndule-api--0000007", "Running"))
        ]);

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Running", updatedParent.State);
            Assert.Equal(KnownResourceState.Running, updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_InitialReplicaRunning_UpdatesParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var child = CreateReplicaChild(parent, "syndule-api--0000007", "Running");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, child], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Running", updatedParent.State);
            Assert.Equal(KnownResourceState.Running, updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_ChildResourceDeleted_RestoresParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var child = CreateReplicaChild(parent, "syndule-api--0000007", "Running");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, child], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Running", updatedParent.State);
            Assert.Equal(KnownResourceState.Running, updatedParent.KnownState);
        });

        channel.Writer.TryWrite([
            new ResourceViewModelChange(ResourceViewModelChangeType.Delete, child)
        ]);

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Scaled to zero", updatedParent.State);
            Assert.Null(updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_ReplicaHealthChanged_UpdatesParentHealthStatus()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var child = CreateReplicaChild(
            parent,
            "syndule-api--0000007",
            "Running",
            ImmutableArray.Create(new HealthReportViewModel("Ready", HealthStatus.Healthy, "Ready", null)));

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, child], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal(HealthStatus.Healthy, updatedParent.HealthStatus);
        });

        channel.Writer.TryWrite([
            new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                CreateReplicaChild(
                    parent,
                    "syndule-api--0000007",
                    "Running",
                    ImmutableArray.Create(new HealthReportViewModel("Ready", HealthStatus.Unhealthy, "Not ready", null))))
        ]);

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Running", updatedParent.State);
            Assert.Equal(KnownResourceState.Running, updatedParent.KnownState);
            Assert.Equal(HealthStatus.Unhealthy, updatedParent.HealthStatus);
        });
    }

    [Fact]
    public void UpdateResources_MultipleRunningReplicas_UsesLeastHealthyReplicaForParentHealth()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var healthyChild = CreateReplicaChild(
            parent,
            "syndule-api--0000001",
            "Running",
            ImmutableArray.Create(new HealthReportViewModel("Ready", HealthStatus.Healthy, "Ready", null)));
        var unhealthyChild = CreateReplicaChild(
            parent,
            "syndule-api--0000002",
            "Running",
            ImmutableArray.Create(new HealthReportViewModel("Ready", HealthStatus.Unhealthy, "Not ready", null)));

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, healthyChild, unhealthyChild], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Running", updatedParent.State);
            Assert.Equal(KnownResourceState.Running, updatedParent.KnownState);
            Assert.Equal(HealthStatus.Unhealthy, updatedParent.HealthStatus);
        });
    }

    [Fact]
    public void UpdateResources_ChildResourceWithDifferentDisplayName_DoesNotUpdateParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("worker", "Project", "Scaled to zero", null);
        var child = CreateChild(parent, "worker-sidecar", "Running", displayName: "worker-sidecar");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, child], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Scaled to zero", updatedParent.State);
            Assert.Null(updatedParent.KnownState);
        });

        channel.Writer.TryWrite([
            new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                CreateChild(parent, "worker-sidecar", "Starting", displayName: "worker-sidecar"))
        ]);

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Scaled to zero", updatedParent.State);
            Assert.Null(updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_TransitoryReplica_BeatsUnhealthyOrFailedReplicaForParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var unhealthyChild = CreateReplicaChild(parent, "syndule-api--0000001", "RuntimeUnhealthy");
        var startingChild = CreateReplicaChild(parent, "syndule-api--0000002", "Starting");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, unhealthyChild, startingChild], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            // Transitory states (Starting) take priority over unhealthy/failed-to-start states,
            // matching the original Running -> transitory -> unhealthy/failed -> any-state cascade.
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("Starting", updatedParent.State);
            Assert.Equal(KnownResourceState.Starting, updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_UnhealthyOrFailedReplica_BeatsOtherKnownStateReplicaForParentState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var exitedChild = CreateReplicaChild(parent, "syndule-api--0000001", "Exited");
        var failedChild = CreateReplicaChild(parent, "syndule-api--0000002", "FailedToStart");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, exitedChild, failedChild], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            // FailedToStart is in the unhealthy/failed-to-start tier, which beats the "any other
            // known state" fallback tier that Exited falls into.
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("FailedToStart", updatedParent.State);
            Assert.Equal(KnownResourceState.FailedToStart, updatedParent.KnownState);
        });
    }

    [Fact]
    public void UpdateResources_RunningReplicasWithEqualHealth_TieBrokenByLowestReplicaIndex()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        // Both replicas have no health reports, so both default to Healthy: the tie must be broken by
        // replica index, not by insertion or dictionary iteration order.
        var higherIndexChild = CreateReplicaChild(parent, "syndule-api--0000002", "Running", replicaIndex: 1, stateStyle: "from-replica-1");
        var lowerIndexChild = CreateReplicaChild(parent, "syndule-api--0000001", "Running", replicaIndex: 0, stateStyle: "from-replica-0");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, higherIndexChild, lowerIndexChild], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("from-replica-0", updatedParent.StateStyle);
        });
    }

    [Fact]
    public void UpdateResources_RunningReplicasWithEqualHealthAndReplicaIndex_TieBrokenByName()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parent = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        // Same health (both default to Healthy with no reports) and the same replica index: the final
        // tie-break is resource name using StringComparers.ResourceName (ordinal, case-insensitive).
        var childB = CreateReplicaChild(parent, "syndule-api--b", "Running", stateStyle: "from-child-b");
        var childA = CreateReplicaChild(parent, "syndule-api--a", "Running", stateStyle: "from-child-a");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parent, childB, childA], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var updatedParent = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parent.Name);
            Assert.Equal("from-child-a", updatedParent.StateStyle);
        });
    }

    [Fact]
    public void UpdateResources_MultipleParentsInSameBatch_EachTracksItsOwnReplicaChildrenIndependently()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parentA = CreateResource("syndule-api", "Azure Container App", "Scaled to zero", null);
        var parentB = CreateResource("syndule-worker", "Azure Container App", "Scaled to zero", null);
        var childA = CreateReplicaChild(parentA, "syndule-api--0000001", "Scaled to zero");
        var childB = CreateReplicaChild(parentB, "syndule-worker--0000001", "Scaled to zero");

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [parentA, parentB, childA, childB], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // A single batch flips childA to Running and childB to RuntimeUnhealthy: verifies the
        // per-batch parent-name -> children lookup groups each parent's replicas correctly and doesn't
        // let one parent's children affect another parent's computed state.
        channel.Writer.TryWrite([
            new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, CreateReplicaChild(parentA, "syndule-api--0000001", "Running")),
            new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, CreateReplicaChild(parentB, "syndule-worker--0000001", "RuntimeUnhealthy")),
        ]);

        cut.WaitForAssertion(() =>
        {
            var updatedParentA = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parentA.Name);
            Assert.Equal("Running", updatedParentA.State);
            Assert.Equal(KnownResourceState.Running, updatedParentA.KnownState);

            var updatedParentB = Assert.Single(cut.Instance.GetFilteredResources(), r => r.Name == parentB.Name);
            Assert.Equal("RuntimeUnhealthy", updatedParentB.State);
            Assert.Equal(KnownResourceState.RuntimeUnhealthy, updatedParentB.KnownState);
        });
    }

    [Fact]
    public void FilterResources()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource(
                "Resource1",
                "Type1",
                "Running",
                ImmutableArray.Create(new HealthReportViewModel("Null", null, "Description1", null))),
            CreateResource(
                "Resource2",
                "Type2",
                "Running",
                ImmutableArray.Create(new HealthReportViewModel("Healthy", HealthStatus.Healthy, "Description2", null))),
            CreateResource(
                "Resource3",
                "Type3",
                "Stopping",
                ImmutableArray.Create(new HealthReportViewModel("Degraded", HealthStatus.Degraded, "Description3", null))),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Open the resource filter
        cut.Find("#resourceFilterButton").Click();

        // Assert 1 (the correct filter options are shown)
        AssertResourceFilterListEquals(cut, [
            new("Type1", true),
            new("Type2", true),
            new("Type3", true),
        ], [
            new("Running", true),
            new("Stopping", true),
        ], [
            new("", true),
            new("Healthy", true),
            new("Unhealthy", true),
        ]);

        // Assert 2 (unselect a resource type, assert that a resource was removed)
        cut.FindComponents<SelectResourceOptions<string>>().First(f => f.Instance.Id == "resource-states")
            .FindComponents<FluentCheckbox>()
            .First(checkbox => checkbox.Instance.Label == "Stopping")
            .Find("fluent-checkbox")
            .TriggerEvent("oncheckedchange", new CheckboxChangeEventArgs { Checked = false });

        // above is triggered asynchronously, so wait for the state to change
        cut.WaitForState(() => cut.Instance.GetFilteredResources().Count() == 2);
    }

    [Fact]
    public void ResourceGraph_MultipleRenders_InitializeOnce()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource(
                "Resource1",
                "Type1",
                "Running",
                ImmutableArray.Create(new HealthReportViewModel("Null", null, "Description1", null))),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        var resourceGraphModule = JSInterop.SetupModule("/js/app-resourcegraph.js");
        var initializeGraphInvocationHandler = resourceGraphModule.SetupVoid("initializeResourcesGraph", _ => true);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourcesUrl(view: "Graph"));

        // Act
        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.Render();

        // Assert
        Assert.Single(initializeGraphInvocationHandler.Invocations);
        var focusInvocation = JSInterop.Invocations.Single(i => i.Identifier == "focusElement");
        Assert.Equal("resourcesGraphContainer", focusInvocation.Arguments[0]);
        Assert.Equal(true, focusInvocation.Arguments[1]);
    }

    [Fact]
    public async Task ResourceGraphContextMenu_MenuCloseCompletesBrowserCallback()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resource = CreateResource(
            "Resource1",
            "Type1",
            "Running",
            ImmutableArray.Create(new HealthReportViewModel("Null", null, "Description1", null)));
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [resource], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        var resourceGraphModule = JSInterop.SetupModule("/js/app-resourcegraph.js");
        resourceGraphModule.SetupVoid("initializeResourcesGraph", _ => true);
        resourceGraphModule.SetupVoid("updateResourcesGraph", _ => true);
        resourceGraphModule.SetupVoid("selectResource", _ => true);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourcesUrl(view: "Graph"));

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var showContextMenuAsync = typeof(Components.Pages.Resources)
            .GetMethod("ShowContextMenuAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task? showContextMenuTask = null;
        await cut.InvokeAsync(() =>
        {
            showContextMenuTask = (Task)showContextMenuAsync.Invoke(cut.Instance, [resource, 1024, 768, 20, 20])!;
        });
        Assert.NotNull(showContextMenuTask);
        cut.WaitForAssertion(() => Assert.True(cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored).Instance.Open));

        var contextMenu = cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored);
        await cut.InvokeAsync(() => contextMenu.Instance.OpenChanged.InvokeAsync(false));

        await showContextMenuTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored).Instance.Open);
    }

    [Fact]
    public void TableView_FocusesAccessibleScrollContainerOnInitialRender()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var scrollContainer = cut.Find("#resourcesScrollContainer");
        var loc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.Resources>>();

        Assert.Equal("0", scrollContainer.GetAttribute("tabindex"));
        Assert.Equal("region", scrollContainer.GetAttribute("role"));
        Assert.Equal(loc[nameof(Dashboard.Resources.Resources.ResourcesHeader)].Value, scrollContainer.GetAttribute("aria-label"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 2 &&
                string.Equals(invocation.Arguments[0]?.ToString(), "resourcesScrollContainer", StringComparison.Ordinal) &&
                string.Equals(invocation.Arguments[1]?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData(false, true, "vertical")]
    [InlineData(true, true, "vertical")]
    [InlineData(false, false, "horizontal")]
    [InlineData(true, false, "horizontal")]
    public void ResourceTabs_OrientationRespondsToUltraLowWidth(bool isDesktop, bool isUltraLowWidth, string expectedOrientation)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: isUltraLowWidth);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource(
                "Resource1",
                "Type1",
                "Running",
                ImmutableArray.Create(new HealthReportViewModel("Null", null, "Description1", null))),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var tabs = cut.Find("fluent-tabs.resources-tab-header");
        Assert.Equal(expectedOrientation, tabs.GetAttribute("orientation"));
        Assert.All(cut.FindAll("fluent-tab"), tab => Assert.True(tab.HasAttribute("fixed")));
    }

    [Fact]
    public void ResourceFilters_ApplyExistingFiltersOnInitialRender()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("Resource1", "Type1", "Running", null),
            CreateResource("Resource2", "Type2", "Finished", null),
        };

        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var sessionStorage = (TestSessionStorage)Services.GetRequiredService<ISessionStorage>();
        // Simulate existing filters in session storage
        sessionStorage.OnGetAsync = key =>
        {
            if (key is BrowserStorageKeys.ResourcesPageState)
            {
                return (true,
                    new Components.Pages.Resources.ResourcesPageState
                    {
                        ResourceTypesToVisibility =
                            new Dictionary<string, bool> { { "Type1", true }, { "Type2", false } },
                        ResourceStatesToVisibility =
                            new Dictionary<string, bool> { { "Running", true }, { "Finished", false } },
                        ResourceHealthStatusesToVisibility =
                            new Dictionary<string, bool> { { "Healthy", true }, { "Unhealthy", false } },
                        ViewKind = null,
                    });
            }

            return (false, null);
        };

        // Act and assert
        var cut = RenderComponent<Components.Pages.Resources>(builder => { builder.AddCascadingValue(viewport); });

        Assert.Collection(cut.Instance.PageViewModel.ResourceTypesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Type1", kvp.Key);
                Assert.True(kvp.Value);
            },
            kvp =>
            {
                Assert.Equal("Type2", kvp.Key);
                Assert.False(kvp.Value);
            });
        Assert.Collection(cut.Instance.PageViewModel.ResourceStatesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal("Finished", kvp.Key);
                Assert.False(kvp.Value);
            },
            kvp =>
            {
                Assert.Equal("Running", kvp.Key);
                Assert.True(kvp.Value);
            });

        // Unhealthy not included because it's not present in any resource
        Assert.Collection(cut.Instance.PageViewModel.ResourceHealthStatusesToVisibility.OrderBy(kvp => kvp.Key),
            kvp =>
            {
                Assert.Equal(string.Empty, kvp.Key);
                Assert.True(kvp.Value);
            },
            kvp =>
            {
                Assert.Equal("Healthy", kvp.Key);
                Assert.True(kvp.Value);
            });
    }

    private static void AssertResourceFilterListEquals(IRenderedComponent<Components.Pages.Resources> cut, IEnumerable<KeyValuePair<string, bool>> types, IEnumerable<KeyValuePair<string, bool>> states, IEnumerable<KeyValuePair<string, bool>> healthStates)
    {
        IReadOnlyList<IRenderedComponent<SelectResourceOptions<string>>> filterComponents = null!;

        cut.WaitForState(() =>
        {
            filterComponents = cut.FindComponents<SelectResourceOptions<string>>();
            return filterComponents.Count == 3;
        });

        var typeSelect = filterComponents.First(f => f.Instance.Id == "resource-types");
        Assert.Equal(types, typeSelect.Instance.Values.ToImmutableSortedDictionary() /* sort for equality comparison */ );

        var stateSelect = filterComponents.First(f => f.Instance.Id == "resource-states");
        Assert.Equal(states, stateSelect.Instance.Values.ToImmutableSortedDictionary() /* sort for equality comparison */);

        var healthSelect = filterComponents.First(f => f.Instance.Id == "resource-health-states");
        Assert.Equal(healthStates, healthSelect.Instance.Values.ToImmutableSortedDictionary() /* sort for equality comparison */);
    }

    [Fact]
    public void ResourcesShouldRemainUnchangedWhenFilterDoesNotMatchUpdatedResource()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("Resource1", "Type1", "Running", null),
            CreateResource("Resource2", "Type2", "Stopping", null),
        };
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: () => channel);

        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Open the resource filter and apply a filter
        cut.Find("#resourceFilterButton").Click();
        cut.FindComponents<SelectResourceOptions<string>>()
            .First(f => f.Instance.Id == "resource-types")
            .FindComponents<FluentCheckbox>()
            .First(checkbox => checkbox.Instance.Label == "Type1")
            .Find("fluent-checkbox")
            .TriggerEvent("oncheckedchange", new CheckboxChangeEventArgs { Checked = false });

        cut.WaitForState(() => cut.Instance.GetFilteredResources().Count() == 1);

        // Act
        channel.Writer.TryWrite(new[]
        {
            new ResourceViewModelChange(
                ResourceViewModelChangeType.Upsert,
                CreateResource("Resource3", "Type3", "Running", null))
        });

        cut.WaitForState(() => cut.Instance.GetFilteredResources().Count() == 2);

        // Assert
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Contains(filteredResources, r => r.Name == "Resource2");
        Assert.Contains(filteredResources, r => r.Name == "Resource3");
    }

    [Fact]
    public void UnreadLogErrorsBadge_StopsKeyboardPropagation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        AddErrorLog(telemetryRepository, resourceName: "Resource1");
        var unviewedErrorCounts = telemetryRepository.GetResourceUnviewedErrorLogsCount();
        var resourceKey = Assert.Single(unviewedErrorCounts.Keys);
        var resource = CreateResource(resourceKey.GetCompositeName(), "Type1", "Running", null);
        Assert.NotNull(telemetryRepository.GetResourceByCompositeName(resource.Name));

        var cut = RenderComponent<UnreadLogErrorsBadge>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.UnviewedErrorCounts, unviewedErrorCounts);
        });

        var badge = cut.Find(".unread-logs-errors-link");
        Assert.Contains("onkeydown:stoppropagation", badge.OuterHtml, StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceViewModel CreateResource(
        string name,
        string type,
        string? state,
        ImmutableArray<HealthReportViewModel>? healthReports,
        bool isHidden = false,
        string? stateStyle = null,
        ImmutableDictionary<string, ResourcePropertyViewModel>? properties = null,
        int? replicaIndex = null,
        string? displayName = null)
    {
        return new ResourceViewModel
        {
            Name = name,
            ResourceType = type,
            State = state,
            KnownState = state is not null && Enum.TryParse<KnownResourceState>(state, out var knownState) ? knownState : null,
            DisplayName = displayName ?? name,
            Uid = name,
            ReplicaIndex = replicaIndex ?? 0,
            HealthReports = healthReports ?? [],

            StateStyle = stateStyle,
            CreationTimeStamp = null,
            StartTimeStamp = null,
            StopTimeStamp = null,
            Environment = [],
            Urls = [],
            Volumes = [],
            Relationships = [],
            Properties = properties ?? ImmutableDictionary<string, ResourcePropertyViewModel>.Empty,
            Commands = [],
            IsHidden = isHidden,
        };
    }

    private static ResourceViewModel CreateReplicaChild(ResourceViewModel parent, string childName, string? state, ImmutableArray<HealthReportViewModel>? healthReports = null, int? replicaIndex = null, string? stateStyle = null)
    {
        return CreateChild(parent, childName, state, parent.DisplayName, healthReports, replicaIndex, stateStyle);
    }

    private static ResourceViewModel CreateChild(ResourceViewModel parent, string childName, string? state, string displayName, ImmutableArray<HealthReportViewModel>? healthReports = null, int? replicaIndex = null, string? stateStyle = null)
    {
        return CreateResource(
            childName,
            parent.ResourceType,
            state,
            healthReports,
            stateStyle: stateStyle,
            replicaIndex: replicaIndex,
            displayName: displayName,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.ParentName] = CreateParentNameProperty(parent.Name)
            }.ToImmutableDictionary());
    }

    private static ResourcePropertyViewModel CreateParentNameProperty(string parentName)
    {
        return new ResourcePropertyViewModel(
            KnownProperties.Resource.ParentName,
            ProtobufValue.ForString(parentName),
            isValueSensitive: false,
            knownProperty: null,
            sortOrder: 0,
            displayName: null,
            isHighlighted: false);
    }

    private static void AddErrorLog(TelemetryRepository repository, string resourceName)
    {
        var addContext = new AddContext();
        var logs = new RepeatedField<ResourceLogs>();
        logs.Add(new ResourceLogs
        {
            Resource = TelemetryTestHelpers.CreateResource(name: resourceName, instanceId: resourceName),
            ScopeLogs =
            {
                new ScopeLogs
                {
                    Scope = TelemetryTestHelpers.CreateScope("TestLogger"),
                    LogRecords =
                    {
                        TelemetryTestHelpers.CreateLogRecord(
                            time: DateTime.UtcNow,
                            message: "Error",
                            severity: SeverityNumber.Error)
                    }
                }
            }
        });

        repository.AddLogs(addContext, logs);

        Assert.Equal(0, addContext.FailureCount);
    }

    [Fact]
    public void ViewOptionsMenu_WiresFocusRestorationWhenHiddenResourcesExist()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("Resource1", "Type1", "Running", null),
            CreateResource("HiddenResource", "Type2", null, null, isHidden: true), // Hidden resource without parent relationship
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(
            this,
            viewport,
            dashboardClient);

        // Act
        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var menuButton = cut.FindComponent<AspireMenuButton>();
        Assert.True(menuButton.Instance.RestoreFocusOnItemClick);
    }

    [Fact]
    public void TableView_ExcludesParameters()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myapp", "Project", "Running", null),
            CreateResource("mycontainer", "Container", "Running", null),
            CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        // Act
        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Assert - Table view (default) should exclude parameters
        Assert.Equal(Components.Pages.Resources.ResourceViewKind.Table, cut.Instance.PageViewModel.SelectedViewKind);
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Equal(2, filteredResources.Count);
        Assert.Contains(filteredResources, r => r.Name == "myapp");
        Assert.Contains(filteredResources, r => r.Name == "mycontainer");
    }

    [Fact]
    public void ParametersView_ShowsOnlyParameters()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myapp", "Project", "Running", null),
            CreateResource("mycontainer", "Container", "Running", null),
            CreateResource("myparameter1", KnownResourceTypes.Parameter, "Running", null),
            CreateResource("myparameter2", KnownResourceTypes.Parameter, "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Parameters view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Parameters;
        cut.Render();

        // Assert - Parameters view should show only parameters
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Equal(2, filteredResources.Count);
        Assert.Contains(filteredResources, r => r.Name == "myparameter1");
        Assert.Contains(filteredResources, r => r.Name == "myparameter2");
    }

    [Fact]
    public void ParametersView_IgnoresResourceTypeFilter()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myapp", "Project", "Running", null),
            CreateResource("myparameter1", KnownResourceTypes.Parameter, "Running", null),
            CreateResource("myparameter2", KnownResourceTypes.Parameter, "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Parameters view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Parameters;
        
        // Set the parameter type filter to false (which would normally hide parameters)
        cut.Instance.PageViewModel.ResourceTypesToVisibility[KnownResourceTypes.Parameter] = false;
        cut.Render();

        // Assert - Parameters view should still show all parameters, ignoring the resource type filter
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Equal(2, filteredResources.Count);
        Assert.Contains(filteredResources, r => r.Name == "myparameter1");
        Assert.Contains(filteredResources, r => r.Name == "myparameter2");
    }

    [Fact]
    public void GraphView_ExcludesParameters()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myapp", "Project", "Running", null),
            CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var resourceGraphModule = JSInterop.SetupModule("/js/app-resourcegraph.js");
        resourceGraphModule.SetupVoid("initializeResourcesGraph", _ => true);
        resourceGraphModule.SetupVoid("updateResourcesGraph", _ => true);
        resourceGraphModule.SetupVoid("updateResourcesGraphSelected", _ => true);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Graph view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Graph;
        cut.Render();

        // Assert - Graph view should exclude parameters (they have their own dedicated view)
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Single(filteredResources);
        Assert.Contains(filteredResources, r => r.Name == "myapp");
    }

    [Fact]
    public void GetVisibleViewKindForSelectedResource_GraphParameter_ReturnsParameters()
    {
        var parameter = CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null);

        var viewKind = Components.Pages.Resources.GetVisibleViewKindForSelectedResource(Components.Pages.Resources.ResourceViewKind.Graph, parameter);

        Assert.Equal(Components.Pages.Resources.ResourceViewKind.Parameters, viewKind);
    }

    [Fact]
    public void GetVisibleViewKindForSelectedResource_GraphNonParameter_ReturnsGraph()
    {
        var resource = CreateResource("myapp", "Project", "Running", null);

        var viewKind = Components.Pages.Resources.GetVisibleViewKindForSelectedResource(Components.Pages.Resources.ResourceViewKind.Graph, resource);

        Assert.Equal(Components.Pages.Resources.ResourceViewKind.Graph, viewKind);
    }

    [Fact]
    public void GetVisibleViewKindForViewChange_GraphParameter_ReturnsParameters()
    {
        var parameter = CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null);

        var viewKind = Components.Pages.Resources.GetVisibleViewKindForViewChange(Components.Pages.Resources.ResourceViewKind.Graph, parameter);

        Assert.Equal(Components.Pages.Resources.ResourceViewKind.Parameters, viewKind);
    }

    [Fact]
    public void GetVisibleViewKindForViewChange_ParametersNonParameter_ReturnsParameters()
    {
        var resource = CreateResource("myapp", "Project", "Running", null);

        var viewKind = Components.Pages.Resources.GetVisibleViewKindForViewChange(Components.Pages.Resources.ResourceViewKind.Parameters, resource);

        Assert.Equal(Components.Pages.Resources.ResourceViewKind.Parameters, viewKind);
    }

    [Fact]
    public void ParametersView_IncludesParametersWithValues()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parameterProperties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty
            .Add(KnownProperties.Parameter.Value, new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                ProtobufValue.ForString("my-secret-value"),
                isValueSensitive: true,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false));

        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null, stateStyle: "success", properties: parameterProperties),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Parameters view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Parameters;
        cut.Render();

        // Assert - The parameter should be displayed in Parameters view
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Single(filteredResources);
        Assert.Equal("myparameter", filteredResources[0].Name);

        // Verify the resource has the expected properties for value display
        var resource = filteredResources[0];
        Assert.True(resource.Properties.ContainsKey(KnownProperties.Parameter.Value));
        Assert.Equal("my-secret-value", resource.Properties[KnownProperties.Parameter.Value].Value.StringValue);
        Assert.True(resource.Properties[KnownProperties.Parameter.Value].IsValueSensitive);
        Assert.Equal("success", resource.StateStyle);
    }

    [Fact]
    public void ParametersView_UrlValueStopsClickPropagation()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var parameterProperties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty
            .Add(KnownProperties.Parameter.Value, new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                ProtobufValue.ForString("https://example.com"),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false));

        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myparameter", KnownResourceTypes.Parameter, "Running", null, properties: parameterProperties),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);
        var setCellTextClickHandler = JSInterop.SetupVoid("setCellTextClickHandler", _ => true);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourcesUrl(view: "Parameters"));

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() => Assert.Single(setCellTextClickHandler.Invocations));
    }

    [Fact]
    public void ParametersView_IncludesUnresolvedParameters()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        // Unresolved parameter has warning stateStyle and exception message as value
        var parameterProperties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty
            .Add(KnownProperties.Parameter.Value, new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                ProtobufValue.ForString("Parameter 'myparameter' not found in configuration."),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false));

        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myparameter", KnownResourceTypes.Parameter, nameof(KnownResourceState.ValueMissing), null, stateStyle: "warning", properties: parameterProperties),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Parameters view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Parameters;
        cut.Render();

        // Assert - The unresolved parameter should be displayed in Parameters view
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Single(filteredResources);
        Assert.Equal("myparameter", filteredResources[0].Name);

        // Verify the resource has warning stateStyle (triggers "Value not set" display)
        var resource = filteredResources[0];
        Assert.Equal("warning", resource.StateStyle);
        Assert.Equal(nameof(KnownResourceState.ValueMissing), resource.State);
    }

    [Fact]
    public void ParametersView_IncludesErrorParameters()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        // Error parameter has error stateStyle
        var parameterProperties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty
            .Add(KnownProperties.Parameter.Value, new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                ProtobufValue.ForString("Error initializing parameter"),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false));

        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("myparameter", KnownResourceTypes.Parameter, "Error", null, stateStyle: "error", properties: parameterProperties),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Act - switch to Parameters view
        cut.Instance.PageViewModel.SelectedViewKind = Components.Pages.Resources.ResourceViewKind.Parameters;
        cut.Render();

        // Assert - The error parameter should be displayed in Parameters view
        var filteredResources = cut.Instance.GetFilteredResources().ToList();
        Assert.Single(filteredResources);
        Assert.Equal("myparameter", filteredResources[0].Name);

        // Verify the resource has error stateStyle (triggers "Value not set" display)
        var resource = filteredResources[0];
        Assert.Equal("error", resource.StateStyle);
    }

    [Fact]
    public void CollapsedResourceNames_FetchedAfterDashboardClientConnected_KeyIncludesApplicationName()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("Resource1", "Type1", "Running", null),
        };

        var connectionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string applicationName = "MyTestApplication";

        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            applicationName: applicationName,
            initialResources: initialResources,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            whenConnected: connectionTcs.Task);

        var collapsedResourceNamesKeyUsed = string.Empty;
        var getAsyncCallOrder = new List<(string Key, bool ConnectionCompleted)>();

        var localStorage = new TestLocalStorage
        {
            OnGetAsync = key =>
            {
                // Track every GetAsync call and whether the connection was completed at that time
                getAsyncCallOrder.Add((key, connectionTcs.Task.IsCompleted));
                if (key.Contains(BrowserStorageKeys.CollapsedResourceNamesKeyPrefix))
                {
                    collapsedResourceNamesKeyUsed = key;
                }
                return (false, null);
            }
        };

        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient, localStorage);

        // Complete the connection immediately so the component can initialize
        connectionTcs.SetResult();

        // Act - Render the component
        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Assert 1 - The key should include the application name
        var expectedKey = BrowserStorageKeys.CollapsedResourceNamesKey(applicationName);
        Assert.Equal(expectedKey, collapsedResourceNamesKeyUsed);

        // Assert 2 - CollapsedResourceNames was only fetched after connection was completed
        var collapsedResourceNamesCall = getAsyncCallOrder.FirstOrDefault(c => c.Key.Contains(BrowserStorageKeys.CollapsedResourceNamesKeyPrefix));
        Assert.NotEqual(default, collapsedResourceNamesCall);
        Assert.True(collapsedResourceNamesCall.ConnectionCompleted,
            "CollapsedResourceNames was fetched before the dashboard client was connected");
    }
}
