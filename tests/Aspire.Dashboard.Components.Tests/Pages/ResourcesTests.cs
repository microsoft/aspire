// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Model.ResourceGraph;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Serialization;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using TelemetryTestHelpers = Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class ResourcesTests : DashboardTestContext
{
    [Theory]
    [InlineData(Components.Pages.Resources.ResourceViewKind.Graph)]
    [InlineData(Components.Pages.Resources.ResourceViewKind.Parameters)]
    public async Task Resources_TelemetryRecordsSelectedView(Components.Pages.Resources.ResourceViewKind viewKind)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources:
        [
            CreateResource("private-resource-a", "private-type-a", "Running", null),
            CreateResource("private-resource-b", "private-type-b", "Running", null)
        ], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);
        using var fixture = new DashboardTelemetryFixture();
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => cut.Instance.TelemetryContext.Initialize(fixture.Telemetry, browserUserAgent: null));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var initializeLog));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, initializeLog.Message);

        using var activity = fixture.Telemetry.StartReportedActivity("resources");
        Assert.NotNull(activity);
        await cut.InvokeAsync(() =>
        {
            cut.Instance.PageViewModel.SelectedViewKind = viewKind;
            cut.Instance.UpdateTelemetryProperties();
        });

        var expectedView = viewKind.ToString();
        Assert.Collection(cut.Instance.TelemetryContext.Properties.OrderBy(p => p.Key, StringComparer.Ordinal),
            property => Assert.Equal(TelemetryPropertyKeys.DashboardComponentId, property.Key),
            property => Assert.Equal(TelemetryPropertyKeys.DashboardComponentType, property.Key),
            property => Assert.Equal(new KeyValuePair<string, AspireTelemetryProperty>(TelemetryPropertyKeys.ResourceView,
                new AspireTelemetryProperty(expectedView, AspireTelemetryPropertyType.UserSetting)), property));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.ParametersSet, log.Message);
        Assert.Collection(log.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal),
            property => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, property.Key),
            property => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.DashboardComponentId, TelemetryComponentIds.Resources), property),
            property => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.DashboardComponentType, nameof(Components.Pages.ComponentType.Page)), property),
            property => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ResourceView, expectedView), property),
            property => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, property.Key),
            property => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ParametersSet), property),
            property => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ParametersSet), property));
        Assert.Equal(activity.TraceId, log.TraceId);
        Assert.Equal(activity.SpanId, log.SpanId);
        Assert.Empty(activity.Events);
        await cut.InvokeAsync(cut.Instance.UpdateTelemetryProperties);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        cut.Dispose();
    }

    [Fact]
    public void ResourceOptions_SelectionChanges_UpdateAllCheckboxState()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentCheckbox(this);
        var values = new ConcurrentDictionary<string, bool>();
        values["Container"] = true;
        values["Project"] = true;

        var cut = Render<SelectResourceOptions<string>>(builder => builder
            .Add(component => component.Values, values)
            .Add(component => component.OnAllValuesCheckedChangedAsync, () => Task.CompletedTask)
            .Add(component => component.OnValueVisibilityChangedAsync, (_, _) => Task.CompletedTask));

        var allCheckbox = cut.FindComponents<FluentCheckbox>()[0].Instance;
        Assert.True(allCheckbox.Value);
        Assert.True(allCheckbox.CheckState);
        Assert.NotNull(cut.Find("fluent-checkbox[title='Container']"));
        Assert.NotNull(cut.Find("fluent-checkbox[title='Project']"));

        values["Container"] = false;
        cut.Render(builder => builder.Add(component => component.Values, values));

        Assert.False(allCheckbox.Value);
        Assert.Null(allCheckbox.CheckState);

        values["Project"] = false;
        cut.Render(builder => builder.Add(component => component.Values, values));

        Assert.False(allCheckbox.Value);
        Assert.False(allCheckbox.CheckState);

        values["Container"] = true;
        values["Project"] = true;
        cut.Render(builder => builder.Add(component => component.Values, values));

        Assert.True(allCheckbox.Value);
        Assert.True(allCheckbox.CheckState);
    }

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
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));

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
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var grid = cut.FindComponent<AspireFluentDataGrid<ResourceGridViewModel>>();
        var nameColumn = Assert.Single(
            cut.FindComponents<AspireTemplateColumn<ResourceGridViewModel>>(),
            column => string.Equals(column.Instance.Title, "Name", StringComparison.Ordinal));

        Assert.Equal("none", cut.Find("th[col-index='1']").GetAttribute("aria-sort"));

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(nameColumn.Instance, DataGridSortDirection.Descending));

        Assert.False(Assert.Single(grid.Instance.SortColumns).Ascending);
        Assert.Equal("descending", cut.Find("th[col-index='1']").GetAttribute("aria-sort"));

        var request = new GridItemsProviderRequest<ResourceGridViewModel>
        {
            SortColumns = [new(nameColumn.Instance, Ascending: false)],
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

        var cut = Render<ResourceActions>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        Assert.NotNull(cut.Find(".resources-filter-popup-container"));

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
        initializeGraphInvocationHandler.SetVoidResult();
        var updateGraphInvocationHandler = resourceGraphModule.SetupVoid("updateResourcesGraph", _ => true);
        updateGraphInvocationHandler.SetVoidResult();
        resourceGraphModule.SetupVoid("updateResourcesGraphSelected", _ => true).SetVoidResult();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourcesUrl(view: "Graph"));

        // Act
        var cut = Render<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.Render();

        // Assert
        Assert.Single(initializeGraphInvocationHandler.Invocations);
        var focusInvocation = JSInterop.Invocations.Single(i => i.Identifier == "focusElement");
        Assert.Equal("resourcesGraphContainer", focusInvocation.Arguments[0]);
        Assert.Equal(true, focusInvocation.Arguments[1]);

        var icons = Assert.IsType<GraphIconsDto>(initializeGraphInvocationHandler.Invocations.Single().Arguments[1]);
        var serializedIcons = JsonSerializer.SerializeToElement(icons, DashboardJsonSerializerContext.Default.Options);
        Assert.Equal(icons.Menu.Path, serializedIcons.GetProperty("menu").GetProperty("path").GetString());
        Assert.Equal(icons.Menu.LabelFormat, serializedIcons.GetProperty("menu").GetProperty("labelFormat").GetString());
        Assert.Equivalent(icons, serializedIcons.Deserialize(icons.GetType(), DashboardJsonSerializerContext.Default.Options), strict: true);

        var resources = Assert.IsType<List<ResourceDto>>(updateGraphInvocationHandler.Invocations.Single().Arguments[0]);
        var resource = Assert.Single(resources);
        var serializedResources = JsonSerializer.SerializeToElement(resources, DashboardJsonSerializerContext.Default.Options);
        Assert.Equal(resource.Name, serializedResources[0].GetProperty("name").GetString());
        Assert.Equivalent(resources, serializedResources.Deserialize(resources.GetType(), DashboardJsonSerializerContext.Default.Options), strict: true);
    }

    [Fact]
    public async Task ResourceGraph_UpdatesWaitForInitializationAndHandleCircuitDisconnect()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var testSink = new TestSink();
        Services.AddSingleton<ILogger<Components.Pages.Resources>>(new TestLogger<Components.Pages.Resources>(new TestLoggerFactory(testSink, enabled: true)));
        ResourceSetupHelpers.SetupResourcesPage(this, viewport);
        var initializeGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = false;
        Exception exception = new JSDisconnectedException("The circuit disconnected.");
        Func<Task>? disposeOnInvoke = null;
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = async identifier =>
            {
                if (identifier == "initializeResourcesGraph")
                {
                    await initializeGraph.Task;
                    return;
                }

                if (disposeOnInvoke is { } dispose)
                {
                    await dispose();
                }

                if (disconnected)
                {
                    throw exception;
                }
            }
        };
        var import = TestJSObjectReference.SetupImport(this, "/js/app-resourcegraph.js");
        import.SetResult(module);

        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourcesUrl(view: "Graph"));
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var tabs = cut.FindComponent<FluentTabs>().Instance;
        var tableTab = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Table").Instance;
        var graphTab = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Graph").Instance;
        try
        {
            await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(tableTab));
            await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(graphTab));

            Assert.Equal(["initializeResourcesGraph"], module.Invocations.Select(i => i.Identifier));
        }
        finally
        {
            initializeGraph.TrySetResult();
        }
        cut.WaitForAssertion(() => Assert.Equal(
            ["initializeResourcesGraph", "updateResourcesGraph", "updateResourcesGraphSelected"],
            module.Invocations.Select(i => i.Identifier)));

        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(tableTab));
        disconnected = true;
        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(graphTab));

        Assert.Equal(2, testSink.Writes.Count(record => record.LogLevel == LogLevel.Debug && ReferenceEquals(record.Exception, exception)));

        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(tableTab));
        exception = new ObjectDisposedException(nameof(Components.Pages.Resources));
        disposeOnInvoke = () => cut.Instance.DisposeAsync().AsTask();
        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(graphTab));

        Assert.Single(testSink.Writes, record => record.LogLevel == LogLevel.Debug && ReferenceEquals(record.Exception, exception));
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    public async Task ResourceGraph_ExportMermaid_UsesVisibleGraph(bool isDesktop, bool showHiddenResources, int initializationStage)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var initialResources = new List<ResourceViewModel>
        {
            ModelTestHelpers.CreateResource("api", displayName: "api", relationships:
            [
                new RelationshipViewModel("cache", "Reference"),
                new RelationshipViewModel("hidden", "Reference"),
                new RelationshipViewModel("parameter", "Reference"),
                new RelationshipViewModel("excluded", "Reference")
            ]),
            ModelTestHelpers.CreateResource("cache", displayName: "cache"),
            ModelTestHelpers.CreateResource("hidden", displayName: "hidden", hidden: true),
            ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter),
            ModelTestHelpers.CreateResource("excluded", resourceType: "Excluded")
        };
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: initialResources, resourceChannelProvider: () => channel);
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.ResourcesShowHiddenResources ? (true, showHiddenResources) : (false, null)
        };
        var dialogs = new List<TextVisualizerDialogViewModel>();
        var dialogService = new TestDialogService((content, parameters) =>
        {
            dialogs.Add(Assert.IsType<TextVisualizerDialogViewModel>(content));
            Assert.Equal("min(1000px, 75vw)", parameters.Width);
            Assert.Equal(Aspire.Dashboard.Resources.Resources.ResourcesGraphExportMermaidButton, parameters.Title);
            return Task.CompletedTask;
        });
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient, sessionStorage: sessionStorage, dialogService: dialogService);
        // Export while import (1) or graph initialization (2) is pending, as well as after both complete (0).
        var initializeGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier => identifier == "initializeResourcesGraph" ? initializeGraph.Task : Task.CompletedTask
        };
        var import = TestJSObjectReference.SetupImport(this, "/js/app-resourcegraph.js");
        if (initializationStage != 1)
        {
            import.SetResult(module);
        }
        if (initializationStage != 2)
        {
            initializeGraph.SetResult();
        }

        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/?view=Graph&HiddenTypes=Excluded");
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            if (initializationStage == 1)
            {
                Assert.Single(import.Invocations);
            }
            else
            {
                Assert.Contains(module.Invocations, i => i.Identifier == (initializationStage == 2 ? "initializeResourcesGraph" : "updateResourcesGraph"));
            }
        });

        await cut.Find(".graph-export").ClickAsync(new());

        var dialog = Assert.Single(dialogs);
        Assert.Equal("resources.mmd", dialog.DownloadFileName);
        Assert.Equal(Aspire.Dashboard.Resources.Resources.ResourcesGraphExportMermaidDescription, dialog.MarkdownDescription);
        Assert.Equal(DashboardUIHelpers.PlaintextFormat, dialog.FixedFormat);
        Assert.False(dialog.ContainsSecret);
        Assert.Empty(JSInterop.Invocations["downloadStreamAsFile"]);
        var expectedMermaid = showHiddenResources
            ? "flowchart LR\n    resource0[\"api\"]\n    resource1[\"cache\"]\n    resource2[\"hidden\"]\n    resource0 --> resource1\n    resource0 --> resource2\n"
            : "flowchart LR\n    resource0[\"api\"]\n    resource1[\"cache\"]\n    resource0 --> resource1\n";
        Assert.Equal(expectedMermaid, dialog.Text);
        await dialogService.LastInstance!.CloseAsync();

        if (initializationStage == 1)
        {
            import.SetResult(module);
        }
        if (initializationStage == 2)
        {
            initializeGraph.SetResult();
        }
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, i => i.Identifier == "updateResourcesGraph"));

        if (isDesktop)
        {
            var search = Assert.Single(cut.FindComponents<FluentTextInput>(), input => input.Instance.Name == "resources-search");
            await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("api"));
        }
        else
        {
            await cut.InvokeAsync(() => cut.Instance.PageViewModel.TextFilter = "api");
        }

        await channel.Writer.WriteAsync([new ResourceViewModelChange(
            ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("api", displayName: "api"))]);
        cut.WaitForAssertion(() => Assert.Single(Assert.IsType<List<ResourceDto>>(module.Invocations.Last(i => i.Identifier == "updateResourcesGraph").Arguments[0])));
        await cut.Find(".graph-export").ClickAsync(new());

        Assert.Equal(2, dialogs.Count);
        Assert.Equal("flowchart LR\n    resource0[\"api\"]\n", dialogs[1].Text);
        Assert.Equal(expectedMermaid, dialog.Text);
        await dialogService.LastInstance!.CloseAsync();
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

        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        var resourceGraphModule = JSInterop.SetupModule("/js/app-resourcegraph.js");
        resourceGraphModule.SetupVoid("initializeResourcesGraph", _ => true).SetVoidResult();
        resourceGraphModule.SetupVoid("updateResourcesGraph", _ => true).SetVoidResult();
        resourceGraphModule.SetupVoid("updateResourcesGraphSelected", _ => true).SetVoidResult();
        var menuStateHandler = resourceGraphModule.SetupVoid("updateResourcesGraphContextMenu", _ => true);
        menuStateHandler.SetVoidResult();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourcesUrl(view: "Graph"));

        var cut = Render<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var showContextMenuAsync = typeof(Components.Pages.Resources)
            .GetMethod("ShowContextMenuAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)showContextMenuAsync.Invoke(cut.Instance, [resource, 20, 20, null])!);
        cut.WaitForAssertion(() => Assert.True(cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored).Instance.Open));

        var contextMenu = cut.FindComponents<AspireMenu>().Single(m => !m.Instance.Anchored);
        Assert.Equal(true, menuStateHandler.Invocations.Last().Arguments[0]);
        var headerItem = contextMenu.Instance.Items[0];
        Assert.True(headerItem.IsHeader);
        Assert.Equal("Resource1", headerItem.Text);
        Assert.NotNull(headerItem.Icon);

        await cut.InvokeAsync(() => contextMenu.FindComponent<FluentMenu>().Instance.OnOpenedChangedAsync(false));

        Assert.False(contextMenu.Instance.Open);
        Assert.Equal(false, menuStateHandler.Invocations.Last().Arguments[0]);
        Assert.Empty(cut.FindComponents<FluentOverlay>());
    }

    [Fact]
    public void TableView_FocusesAccessibleScrollContainerOnInitialRender()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var tabs = cut.FindComponent<FluentTabs>();
        Assert.Equal(expectedOrientation, tabs.Instance.Orientation?.ToString().ToLowerInvariant());
        Assert.All(cut.FindAll("fluent-tab"), tab => Assert.False(tab.HasAttribute("fixed")));
    }

    [Fact]
    public async Task MobileParametersTab_UpdatesUrlWithoutOpeningFilterPanel()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport);

        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var layout = cut.FindComponent<AspirePageContentLayout>().Instance;
        var tabs = cut.FindComponent<FluentTabs>().Instance;
        var parametersTab = cut.FindComponents<FluentTab>().Single(tab => tab.Instance.Id == "tab-Parameters");

        Assert.False(layout.IsToolbarPanelOpen);

        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(parametersTab.Instance));

        Assert.EndsWith("/?view=Parameters", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Empty(layout.DialogCloseListeners);
    }

    [Fact]
    public async Task MobileParametersTab_DoesNotNavigateAfterCircuitDisconnects()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (_, _) => throw new JSDisconnectedException("The circuit disconnected.")
        };
        ResourceSetupHelpers.SetupResourcesPage(this, viewport, sessionStorage: sessionStorage);

        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var tabs = cut.FindComponent<FluentTabs>().Instance;
        var parametersTab = cut.FindComponents<FluentTab>().Single(tab => tab.Instance.Id == "tab-Parameters");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var initialUri = navigation.Uri;

        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(parametersTab.Instance));

        Assert.Equal(initialUri, navigation.Uri);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MobileToolbar_PendingViewChangeOnlyNavigatesWhenToolbarCloses(bool navigateAway, bool dispose)
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport);
        FluentUISetupHelpers.SetupFluentDialogProvider(this);
        var provider = Render<DashboardDialogProvider>(builder => builder.AddCascadingValue(viewport));
        var cut = Render<Components.Pages.Resources>(builder => builder.AddCascadingValue(viewport));
        var layout = cut.FindComponent<AspirePageContentLayout>().Instance;
        var tabs = cut.FindComponent<FluentTabs>().Instance;
        var parametersTab = cut.FindComponents<FluentTab>().Single(tab => tab.Instance.Id == "tab-Parameters");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var initialUri = navigation.Uri;

        var opening = cut.InvokeAsync(layout.OpenMobileToolbarAsync);
        var dialog = provider.WaitForComponent<FluentDialog>();
        await provider.InvokeAsync(() => dialog.Find($"#{dialog.Instance.Id}").TriggerEvent("ondialogbeforetoggle", new DialogToggleEventArgs
        {
            Id = dialog.Instance.Id,
            Type = "beforetoggle",
            OldState = "closed",
            NewState = "open"
        }));
        await opening;
        Assert.True(layout.IsToolbarPanelOpen);

        await cut.InvokeAsync(() => tabs.ActiveTabChanged.InvokeAsync(parametersTab.Instance));
        Assert.Equal(initialUri, navigation.Uri);
        Assert.Single(layout.DialogCloseListeners);

        if (dispose)
        {
            await DisposeComponentsAsync();
            Assert.Equal(initialUri, navigation.Uri);
        }
        else if (navigateAway)
        {
            await cut.InvokeAsync(() => navigation.NavigateTo("/traces"));
            provider.WaitForAssertion(() => Assert.False(layout.IsToolbarPanelOpen));
            Assert.EndsWith("/traces", navigation.Uri, StringComparison.Ordinal);
        }
        else
        {
            await cut.InvokeAsync(layout.CloseMobileToolbarAsync);
            Assert.EndsWith("/?view=Parameters", navigation.Uri, StringComparison.Ordinal);
        }

        Assert.Empty(layout.DialogCloseListeners);
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
        var cut = Render<Components.Pages.Resources>(builder => { builder.AddCascadingValue(viewport); });

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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<UnreadLogErrorsBadge>(builder =>
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
        var cut = Render<Components.Pages.Resources>(builder =>
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
        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        Render<GridValue>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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

        var cut = Render<Components.Pages.Resources>(builder =>
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
    public void CollapsedResourceNames_FetchedAfterDashboardClientConnected_KeyIncludesApplicationNameAndHash()
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
        var cut = Render<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Assert 1 - The key should include the application name
        Assert.Matches("^Aspire_Resources_CollapsedResourceNames_mytestapplication-[a-f0-9]{16}$", collapsedResourceNamesKeyUsed);

        // Assert 2 - CollapsedResourceNames was only fetched after connection was completed
        var collapsedResourceNamesCall = getAsyncCallOrder.FirstOrDefault(c => c.Key.Contains(BrowserStorageKeys.CollapsedResourceNamesKeyPrefix));
        Assert.NotEqual(default, collapsedResourceNamesCall);
        Assert.True(collapsedResourceNamesCall.ConnectionCompleted,
            "CollapsedResourceNames was fetched before the dashboard client was connected");
    }
}
