// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
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
    public async Task Resources_DefaultOrderIsTypeThenNameWithChildrenNested()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var childProperties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty
            .Add(KnownProperties.Resource.ParentName, new ResourcePropertyViewModel(
                KnownProperties.Resource.ParentName,
                ProtobufValue.ForString("z-project"),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false));
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("z-project-child", "Executable", "Running", null, properties: childProperties),
            CreateResource("z-project", "Project", "Running", null),
            CreateResource("basketcache", "Container", "Running", null),
            CreateResource("apigateway", "Project", "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);
        var cut = RenderComponent<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));

        var result = await cut.InvokeAsync(() => cut.Instance.GetData(new GridItemsProviderRequest<ResourceGridViewModel>()).AsTask());

        Assert.Collection(
            result.Items,
            item => Assert.Equal("basketcache", item.Resource.Name),
            item => Assert.Equal("apigateway", item.Resource.Name),
            item => Assert.Equal("z-project", item.Resource.Name),
            item => Assert.Equal("z-project-child", item.Resource.Name));
        Assert.Equal(0, result.Items.ElementAt(2).Depth);
        Assert.Equal(1, result.Items.ElementAt(3).Depth);
    }

    [Fact]
    public async Task Resources_NameColumnSortsDescending()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            CreateResource("basketcache", "Container", "Running", null),
            CreateResource("apigateway", "Project", "Running", null),
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);
        var cut = RenderComponent<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var grid = cut.FindComponent<AspireFluentDataGrid<ResourceGridViewModel>>();
        var nameColumn = Assert.Single(
            cut.FindComponents<AspireTemplateColumn<ResourceGridViewModel>>(),
            column => string.Equals(column.Instance.Title, "Name", StringComparison.Ordinal));

        Assert.Equal("none", cut.Find("th[col-index='1']").GetAttribute("aria-sort"));

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(nameColumn.Instance, DataGridSortDirection.Descending));

        Assert.False(grid.Instance.SortByAscending);
        Assert.Equal("descending", cut.Find("th[col-index='1']").GetAttribute("aria-sort"));

        var request = new GridItemsProviderRequest<ResourceGridViewModel>
        {
            SortByColumn = nameColumn.Instance,
            SortByAscending = false,
        };
        var result = await cut.InvokeAsync(() => cut.Instance.GetData(request).AsTask());

        Assert.Collection(
            result.Items,
            item => Assert.Equal("basketcache", item.Resource.Name),
            item => Assert.Equal("apigateway", item.Resource.Name));
    }

    [Fact]
    public void ReadOnly_HighlightedCommandIsVisibleAndDisabled()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            state: KnownResourceState.Running,
            commands:
            [
                new CommandViewModel(
                    "test-command",
                    CommandViewModelState.Enabled,
                    "Test command",
                    "Test command description",
                    confirmationMessage: "",
                    argumentInputs: [],
                    isHighlighted: true,
                    iconName: string.Empty,
                    iconVariant: IconVariant.Regular)
            ]);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources: [resource],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: true);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<ResourceActions>(builder =>
        {
            builder.AddCascadingValue(viewport);
            builder.Add(component => component.CommandSelected, EventCallback.Factory.Create<CommandViewModel>(this, _ => Task.CompletedTask));
            builder.Add(component => component.IsCommandExecuting, (ResourceViewModel _, CommandViewModel _) => false);
            builder.Add(component => component.OnViewDetails, EventCallback.Factory.Create<string?>(this, _ => Task.CompletedTask));
            builder.Add(component => component.Resource, resource);
            builder.Add(component => component.MaxHighlightedCount, 1);
            builder.Add(component => component.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>());
        });

        var commandButton = cut.Find("fluent-button");
        Assert.True(commandButton.HasAttribute("disabled"));
    }

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
    public async Task FilterResources()
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
        var stoppingCheckbox = cut.FindComponents<SelectResourceOptions<string>>().First(f => f.Instance.Id == "resource-states")
            .FindComponents<FluentCheckbox>()
            .First(checkbox => checkbox.Instance.Label == "Stopping");
        await stoppingCheckbox.InvokeAsync(() => stoppingCheckbox.Instance.ValueChanged.InvokeAsync(false));

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
    public async Task ResourceGraphContextMenu_OpensWithoutWaitingForClose()
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

        await cut.InvokeAsync(() => (Task)showContextMenuAsync.Invoke(cut.Instance, [resource, 20, 20, null])!);
        cut.WaitForAssertion(() => Assert.True(cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored).Instance.Open));

        var contextMenu = cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored);
        var headerItem = contextMenu.Instance.Items[0];
        Assert.True(headerItem.IsHeader);
        Assert.Equal("Resource1", headerItem.Text);
        Assert.NotNull(headerItem.Icon);

        await cut.InvokeAsync(() => contextMenu.Instance.OpenChanged.InvokeAsync(false));

        Assert.False(cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored).Instance.Open);
        Assert.Empty(cut.FindComponents<FluentOverlay>());
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

    [Fact]
    public void TableView_RestoresColumnOrderAfterMobileView()
    {
        var desktopViewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, desktopViewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(desktopViewport);
        });

        var desktopColumnOrder = cut.FindComponent<AspireFluentDataGrid<ResourceGridViewModel>>().Instance.GetColumnOrder();

        var mobileViewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(mobileViewport);
        cut.Render();

        var mobileColumnOrder = cut.FindComponent<AspireFluentDataGrid<ResourceGridViewModel>>().Instance.GetColumnOrder();
        Assert.True(mobileColumnOrder.Count < desktopColumnOrder.Count);

        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(desktopViewport);
        cut.Render();

        var restoredColumnOrder = cut.FindComponent<AspireFluentDataGrid<ResourceGridViewModel>>().Instance.GetColumnOrder();
        Assert.Equal(desktopColumnOrder, restoredColumnOrder);
    }

    [Fact]
    public void DesktopFilterControls_AreLabeledGroup()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var filterGroup = cut.Find(".resource-tabs-toolbar");
        var loc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.ControlsStrings>>();

        Assert.Equal("group", filterGroup.GetAttribute("role"));
        Assert.Equal(loc[nameof(Dashboard.Resources.ControlsStrings.PageToolbarLandmark)].Value, filterGroup.GetAttribute("aria-label"));
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

        var tabs = cut.FindComponent<FluentTabs>();
        Assert.Equal(expectedOrientation, tabs.Instance.Orientation?.ToString().ToLowerInvariant());
        Assert.All(cut.FindAll("fluent-tab"), tab => Assert.False(tab.HasAttribute("fixed")));
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
    public async Task ResourcesShouldRemainUnchangedWhenFilterDoesNotMatchUpdatedResource()
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
        var typeCheckbox = cut.FindComponents<SelectResourceOptions<string>>()
            .First(f => f.Instance.Id == "resource-types")
            .FindComponents<FluentCheckbox>()
            .First(checkbox => checkbox.Instance.Label == "Type1");
        await typeCheckbox.InvokeAsync(() => typeCheckbox.Instance.ValueChanged.InvokeAsync(false));

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
    public async Task UnreadLogErrorsBadge_StopsKeyboardPropagation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await AddErrorLog(telemetryRepository, resourceName: "Resource1");
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
        int? replicaIndex = null)
    {
        return new ResourceViewModel
        {
            Name = name,
            ResourceType = type,
            State = state,
            KnownState = state is not null && Enum.TryParse<KnownResourceState>(state, out var knownState) ? knownState : null,
            DisplayName = name,
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

    private static async Task AddErrorLog(SqliteTelemetryRepository repository, string resourceName)
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

        await repository.AddLogsAsync(addContext, logs);

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
    public void GridValue_UrlValueStopsClickPropagation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var setCellTextClickHandler = JSInterop.SetupVoid("setCellTextClickHandler", _ => true);

        RenderComponent<GridValue>(builder =>
        {
            builder.Add(p => p.Value, "https://example.com");
            builder.Add(p => p.ValueDescription, "Parameter value");
            builder.Add(p => p.StopClickPropagation, true);
        });

        Assert.Single(setCellTextClickHandler.Invocations);
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
