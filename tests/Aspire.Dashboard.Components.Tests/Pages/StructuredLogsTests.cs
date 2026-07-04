// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class StructuredLogsTests : DashboardTestContext
{
    [Fact]
    public void Render_ResourceInstanceHasDashes_AppKeyResolvedCorrectly()
    {
        // Arrange
        SetupStructureLogsServices();

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestApp", instanceId: "abc-def"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord()
                        }
                    }
                }
            }
        });

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(resource: "TestApp"));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "TestApp");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        // Assert
        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();

        Assert.NotNull(viewModel.ResourceKey);
        Assert.Equal("TestApp", viewModel.ResourceKey.Value.Name);
        Assert.Equal("abc-def", viewModel.ResourceKey.Value.InstanceId);
    }

    [Fact]
    public void Render_TraceIdAndSpanId_FilterAdded()
    {
        // Arrange
        SetupStructureLogsServices();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(traceId: "123", spanId: "456"));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        // Assert
        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();

        Assert.Collection(viewModel.Filters,
            f =>
            {
                Assert.Equal(KnownStructuredLogFields.TraceIdField, f.Field);
                Assert.Equal("123", f.Value);
            },
            f =>
            {
                Assert.Equal(KnownStructuredLogFields.SpanIdField, f.Field);
                Assert.Equal("456", f.Value);
            });
    }

    [Fact]
    public void Render_DuplicateFilters_SingleFilterAdded()
    {
        // Arrange
        SetupStructureLogsServices();

        var filter = new FieldTelemetryFilter { Field = "TestField", Condition = FilterCondition.Contains, Value = "TestValue" };
        var serializedFilter = TelemetryFilterFormatter.SerializeFiltersToString([filter, filter]);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(filters: serializedFilter));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        // Assert
        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();

        Assert.Collection(viewModel.Filters,
            f =>
            {
                Assert.Equal(filter.Field, f.Field);
                Assert.Equal(filter.Condition, f.Condition);
                Assert.Equal(filter.Value, f.Value);
            });
    }

    [Fact]
    public void Render_RouteResourceBeforeTelemetryArrives_FiltersLogsWhenResourcesArrive()
    {
        SetupStructureLogsServices();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(resource: "TestApp"));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "TestApp");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();
        viewModel.StartIndex = 0;
        viewModel.Count = 100;
        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();

        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "OtherApp", instanceId: "other-instance"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Other resource log")
                        }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("TestApp", viewModel.ResourceKey?.Name);
            Assert.Empty(viewModel.GetLogs().Items);
        });

        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestApp", instanceId: "app-instance"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Selected resource log")
                        }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("TestApp", cut.Instance.PageViewModel.SelectedResource.Name);
            Assert.Equal("TestApp", viewModel.ResourceKey?.Name);

            var logs = viewModel.GetLogs();
            var log = Assert.Single(logs.Items);
            Assert.Equal("TestApp", log.ResourceView.ResourceKey.Name);
            Assert.Equal(1, logs.TotalItemCount);
        });
    }

    [Fact]
    public void Render_RouteReplicaDisplayNameBeforeTelemetryArrives_FiltersLogsWhenResourcesArrive()
    {
        SetupStructureLogsServices();

        var selectedInstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var siblingInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var selectedResourceName = $"TestApp-{selectedInstanceId.ToString("N")[^8..]}";

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(resource: selectedResourceName));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, selectedResourceName);
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();
        viewModel.StartIndex = 0;
        viewModel.Count = 100;
        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();

        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "OtherApp", instanceId: "other-instance"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Other resource log")
                        }
                    }
                }
            }
        });

        cut.WaitForAssertion(() => Assert.Empty(viewModel.GetLogs().Items));

        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestApp", instanceId: selectedInstanceId.ToString()),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Selected resource log")
                        }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestApp", instanceId: siblingInstanceId.ToString()),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Sibling resource log")
                        }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(selectedResourceName, cut.Instance.PageViewModel.SelectedResource.Name);
            Assert.Equal("TestApp", viewModel.ResourceKey?.Name);
            Assert.Equal(selectedInstanceId.ToString(), viewModel.ResourceKey?.InstanceId);

            var logs = viewModel.GetLogs();
            var log = Assert.Single(logs.Items);
            Assert.Equal("TestApp", log.ResourceView.ResourceKey.Name);
            Assert.Equal(selectedInstanceId.ToString(), log.ResourceView.ResourceKey.InstanceId);
            Assert.Equal(1, logs.TotalItemCount);
        });
    }

    [Fact]
    public async Task Render_RouteResourceBeforeTelemetryArrives_SerializesRouteResourceFilter()
    {
        SetupStructureLogsServices();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl(resource: "TestApp"));

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "TestApp");
            builder.Add(p => p.ViewportInformation, viewport);
        });
        var page = cut.Instance;

        Assert.Equal("TestApp", page.ViewModel.ResourceKey?.Name);
        Assert.Null(page.PageViewModel.SelectedResource.Id);

        await cut.InvokeAsync(() => page.AfterViewModelChangedAsync(layout: null, waitToApplyMobileChange: true));

        var state = page.ConvertViewModelToSerializable();
        Assert.Equal("TestApp", state.SelectedResource);
        Assert.EndsWith("/structuredlogs/resource/TestApp", navigationManager.Uri);
    }

    [Fact]
    public async Task Render_RouteResourceAfterTelemetryArrives_AllSelectionClearsSerializedResource()
    {
        SetupStructureLogsServices();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl(resource: "TestApp"));

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "TestApp");
            builder.Add(p => p.ViewportInformation, viewport);
        });
        var page = cut.Instance;
        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();

        telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestApp", instanceId: "app-instance"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "test-scope"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "Selected resource log")
                        }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("TestApp", page.ViewModel.ResourceKey?.Name);
            Assert.NotNull(page.PageViewModel.SelectedResource.Id);
        });

        var allResourceLabel = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.ControlsStrings>>()[nameof(Dashboard.Resources.ControlsStrings.LabelAll)].Value;

        await cut.InvokeAsync(() =>
        {
            var resourceSelect = cut.FindComponent<ResourceSelect>();
            var innerSelect = resourceSelect.Find("fluent-select");
            innerSelect.Change(allResourceLabel);
        });

        cut.WaitForAssertion(() =>
        {
            var state = page.ConvertViewModelToSerializable();
            Assert.Null(state.SelectedResource);
            Assert.EndsWith("/structuredlogs", navigationManager.Uri);
        });
    }

    [Fact]
    public void Render_FiltersWithSpecialCharacters_SuccessfullyParsed()
    {
        // Arrange
        SetupStructureLogsServices();

        var filter1 = new FieldTelemetryFilter { Field = "Test:Field", Condition = FilterCondition.Contains, Value = "Test Value" };
        var filter2 = new FieldTelemetryFilter { Field = "Test!@#", Condition = FilterCondition.Contains, Value = "http://localhost#fragment?hi=true" };
        var filter3 = new FieldTelemetryFilter { Field = "\u2764\uFE0F", Condition = FilterCondition.Contains, Value = "\u4F60" };
        var serializedFilter = TelemetryFilterFormatter.SerializeFiltersToString([filter1, filter2, filter3]);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.ToAbsoluteUri(DashboardUrls.StructuredLogsUrl(filters: serializedFilter));
        navigationManager.NavigateTo(uri.OriginalString);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        // Assert
        var viewModel = Services.GetRequiredService<StructuredLogsViewModel>();

        Assert.Collection(viewModel.Filters,
            f =>
            {
                Assert.Equal(filter1.Field, f.Field);
                Assert.Equal(filter1.Condition, f.Condition);
                Assert.Equal(filter1.Value, f.Value);
            },
            f =>
            {
                Assert.Equal(filter2.Field, f.Field);
                Assert.Equal(filter2.Condition, f.Condition);
                Assert.Equal(filter2.Value, f.Value);
            },
            f =>
            {
                Assert.Equal(filter3.Field, f.Field);
                Assert.Equal(filter3.Condition, f.Condition);
                Assert.Equal(filter3.Value, f.Value);
            });
    }

    [Fact]
    public void Render_FocusesAccessibleScrollContainerOnInitialRender()
    {
        SetupStructureLogsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<StructuredLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var scrollContainer = cut.Find("#structuredLogsScrollContainer");
        var loc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.StructuredLogs>>();

        Assert.Equal("0", scrollContainer.GetAttribute("tabindex"));
        Assert.Equal("region", scrollContainer.GetAttribute("role"));
        Assert.Equal(loc[nameof(Dashboard.Resources.StructuredLogs.StructuredLogsHeader)].Value, scrollContainer.GetAttribute("aria-label"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 2 &&
                string.Equals(invocation.Arguments[0]?.ToString(), "structuredLogsScrollContainer", StringComparison.Ordinal) &&
                string.Equals(invocation.Arguments[1]?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase));
        });
    }

    private void SetupStructureLogsServices()
    {
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        JSInterop.SetupVoid("initializeContinuousScroll").SetVoidResult();
        JSInterop.SetupVoid("resetContinuousScrollPosition").SetVoidResult();
        JSInterop.SetupVoid("focusElement", _ => true);

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILogger<StructuredLogs>>(NullLogger<StructuredLogs>.Instance);
        Services.AddSingleton<StructuredLogsViewModel>();
    }
}
