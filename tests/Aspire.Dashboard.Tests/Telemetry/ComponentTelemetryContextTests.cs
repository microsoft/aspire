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
    public void ComponentTelemetryContext_TelemetryEnabled_EndToEnd()
    {
        // Arrange
        var telemetryContext = new ComponentTelemetryContext(ComponentType.Page, nameof(ComponentTelemetryContextTests));
        using var fixture = new DashboardTelemetryFixture();
        var telemetryService = fixture.Telemetry;
        var telemetryContextProvider = new ComponentTelemetryContextProvider(telemetryService);
        telemetryContextProvider.SetBrowserUserAgent("mozilla");
        var logger = NullLogger<ComponentTelemetryContextTests>.Instance;

        // Act & assert initialize
        telemetryContextProvider.Initialize(telemetryContext);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var initializeEvent));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, initializeEvent.Message);

        Assert.Equal(3, telemetryContext.Properties.Count);

        // Act & assert update properties
        telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty("Test", new AspireTelemetryProperty("Value"))], logger);
        Assert.Equal(4, telemetryContext.Properties.Count);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var parametersUpdateEvent));
        Assert.Equal(TelemetryEventKeys.ParametersSet, parametersUpdateEvent.Message);

        // If value didn't change, we shouldn't post again
        telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty("Test", new AspireTelemetryProperty("Value"))], logger);
        Assert.Equal(4, telemetryContext.Properties.Count);
        Assert.False(fixture.LogChannel.Reader.TryRead(out parametersUpdateEvent));

        telemetryContext.UpdateTelemetryProperties([new ComponentTelemetryProperty("Test", new AspireTelemetryProperty("NewValue"))], logger);
        Assert.Equal(4, telemetryContext.Properties.Count);
        Assert.True(fixture.LogChannel.Reader.TryRead(out parametersUpdateEvent));

        // Act & assert dispose
        telemetryContext.Dispose();
        Assert.True(fixture.LogChannel.Reader.TryRead(out var disposeEvent));
        Assert.Equal(TelemetryEventKeys.ComponentDispose, disposeEvent.Message);
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
