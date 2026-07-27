// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        JSInterop.SetupVoid("initializeContinuousScroll");

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILogger<StructuredLogs>>(NullLogger<StructuredLogs>.Instance);
        Services.AddSingleton<StructuredLogsViewModel>();
    }
}
