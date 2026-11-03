// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Aspire.TerminalHost.Tests;

[Collection(nameof(TerminalHostAppTestsCollection))]
public class TerminalHostTelemetryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-bool")]
    [InlineData("0")]
    public void TelemetryRequiresExplicitOptIn(string? enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.TerminalHostTelemetryEnabled] = enabled,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["OTEL_SERVICE_NAME"] = "myapp-terminalhost-0",
            ["ASPIRE_TERMINAL_HOST_LOG_LEVEL"] = "Trace",
        }).Build();

        Assert.Null(TerminalHostApp.CreateTelemetryHostBuilder(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TelemetryRequiresOtlpEndpoint(string? endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.TerminalHostTelemetryEnabled] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
        }).Build();

        Assert.Null(TerminalHostApp.CreateTelemetryHostBuilder(configuration));
    }

    [Theory]
    [InlineData("myapp-terminalhost-0", "myapp-terminalhost-0", "true")]
    [InlineData("myapp-terminalhost-1", "myapp-terminalhost-1", "true")]
    [InlineData(null, TerminalHostTelemetry.SourceName, "true")]
    [InlineData("", TerminalHostTelemetry.SourceName, "true")]
    [InlineData("myapp-terminalhost-0", "myapp-terminalhost-0", "1")]
    [InlineData("myapp-terminalhost-0", "myapp-terminalhost-0", "-1")]
    public void EnabledTelemetryRegistersAllSignalsWithResourceIdentity(string? serviceName, string expectedServiceName, string enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.TerminalHostTelemetryEnabled] = enabled,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["OTEL_SERVICE_NAME"] = serviceName,
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.instance.id=test-instance",
            ["ASPIRE_TERMINAL_HOST_LOG_LEVEL"] = "Debug",
        }).Build();

        var builder = Assert.IsType<HostApplicationBuilder>(TerminalHostApp.CreateTelemetryHostBuilder(configuration));
        using var host = builder.Build();
        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();
        Assert.Single(host.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>());

        Assert.Equal(expectedServiceName, tracerProvider.GetResource().Attributes.Single(attribute => attribute.Key == "service.name").Value);
        Assert.Equal(expectedServiceName, meterProvider.GetResource().Attributes.Single(attribute => attribute.Key == "service.name").Value);
        Assert.Equal("test-instance", tracerProvider.GetResource().Attributes.Single(attribute => attribute.Key == "service.instance.id").Value);
        Assert.Equal("test-instance", meterProvider.GetResource().Attributes.Single(attribute => attribute.Key == "service.instance.id").Value);
        Assert.True(TerminalHostTelemetry.ActivitySource.HasListeners());
        Assert.True(TerminalHostTelemetry.UpstreamRecycles.Enabled);
        Assert.True(host.Services.GetRequiredService<ILogger<TerminalHostApp>>().IsEnabled(LogLevel.Debug));
    }
}
