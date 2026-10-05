// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class ComponentTelemetryContextTests
{
    [Fact]
    public void ComponentTelemetryContext_TelemetryEnabled_RecordsLifecycleAndPropertyUpdates()
    {
        var telemetryContext = new ComponentTelemetryContext(ComponentType.Page, nameof(ComponentTelemetryContextTests));
        using var fixture = new DashboardTelemetryFixture();
        var telemetryService = fixture.Telemetry;
        var telemetryContextProvider = new ComponentTelemetryContextProvider(telemetryService);
        telemetryContextProvider.SetBrowserUserAgent("mozilla");
        var logger = NullLogger<ComponentTelemetryContextTests>.Instance;

        telemetryContextProvider.Initialize(telemetryContext);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var initializeEvent));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, initializeEvent.Message);
        Assert.Equal(nameof(ComponentTelemetryContextTests), initializeEvent.Attributes.Single(p => p.Key == TelemetryPropertyKeys.DashboardComponentId).Value);
        Assert.Equal(nameof(ComponentType.Page), initializeEvent.Attributes.Single(p => p.Key == TelemetryPropertyKeys.DashboardComponentType).Value);

        Assert.True(telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsSelectedView, new AspireTelemetryProperty("Graph"))], logger));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var parametersUpdateEvent));
        Assert.Equal(TelemetryEventKeys.ParametersSet, parametersUpdateEvent.Message);
        Assert.Equal("Graph", parametersUpdateEvent.Attributes.Single(p => p.Key == TelemetryPropertyKeys.MetricsSelectedView).Value);

        Assert.False(telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsSelectedView, new AspireTelemetryProperty("Graph"))], logger));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));

        Assert.True(telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsSelectedView, new AspireTelemetryProperty("Table"))], logger));
        Assert.True(fixture.LogChannel.Reader.TryRead(out parametersUpdateEvent));
        Assert.Equal(TelemetryEventKeys.ParametersSet, parametersUpdateEvent.Message);
        Assert.Equal("Table", parametersUpdateEvent.Attributes.Single(p => p.Key == TelemetryPropertyKeys.MetricsSelectedView).Value);

        telemetryContext.Dispose();
        Assert.True(fixture.LogChannel.Reader.TryRead(out var disposeEvent));
        Assert.Equal(TelemetryEventKeys.ComponentDispose, disposeEvent.Message);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void ComponentTelemetryContext_TelemetryDisabled_EndToEnd()
    {
        // Arrange
        var telemetryContext = new ComponentTelemetryContext(ComponentType.Page, nameof(ComponentTelemetryContextTests));
        using var fixture = new DashboardTelemetryFixture(reportedTelemetryEnabled: false);
        var telemetryService = fixture.Telemetry;
        var telemetryContextProvider = new ComponentTelemetryContextProvider(telemetryService);
        telemetryContextProvider.SetBrowserUserAgent("mozilla");
        var logger = NullLogger<ComponentTelemetryContextTests>.Instance;

        // Act & assert initialize
        telemetryContextProvider.Initialize(telemetryContext);
        Assert.False(fixture.LogChannel.Reader.TryRead(out _));

        // Act & assert update properties
        telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty("Test", new AspireTelemetryProperty("Value"))], logger);
        Assert.Collection(telemetryContext.Properties.OrderBy(p => p.Key),
            kvp =>
            {
                Assert.Equal("Aspire.Dashboard.ComponentId", kvp.Key);
                Assert.Equal("ComponentTelemetryContextTests", kvp.Value.Value);
            },
            kvp =>
            {
                Assert.Equal("Aspire.Dashboard.ComponentType", kvp.Key);
                Assert.Equal("Page", kvp.Value.Value);
            },
            kvp =>
            {
                Assert.Equal("Aspire.Dashboard.UserAgent", kvp.Key);
                Assert.Equal("mozilla", kvp.Value.Value);
            },
            kvp =>
            {
                Assert.Equal("Test", kvp.Key);
                Assert.Equal("Value", kvp.Value.Value);
            });
        Assert.False(fixture.LogChannel.Reader.TryRead(out _));

        // Act & assert dispose
        telemetryContext.Dispose();
        Assert.False(fixture.LogChannel.Reader.TryRead(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void ComponentTelemetryContext_DisposeWithoutInitialize_NoThrow()
    {
        // Arrange
        var telemetryContext = new ComponentTelemetryContext(ComponentType.Page, nameof(ComponentTelemetryContextTests));

        // Act
        telemetryContext.Dispose();
    }
}
