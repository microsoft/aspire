// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class WithOtlpExporterTests
{
    [InlineData(default, "http://localhost:8889", null, "http://localhost:8889", "grpc")]
    [InlineData(default, "http://localhost:8889", "http://localhost:8890", "http://localhost:8889", "grpc")]
    [InlineData(default, null, "http://localhost:8890", "http://localhost:8890", "http/protobuf")]
    [InlineData(OtlpProtocol.HttpProtobuf, "http://localhost:8889", "http://localhost:8890", "http://localhost:8890", "http/protobuf")]
    [InlineData(OtlpProtocol.Grpc, "http://localhost:8889", "http://localhost:8890", "http://localhost:8889", "grpc")]
    [InlineData(OtlpProtocol.Grpc, null, null, "http://localhost:18889", "grpc")]
    [InlineData(OtlpProtocol.HttpJson, "http://localhost:8889", "http://localhost:8890", "http://localhost:8890", "http/json")]
    [Theory]
    public async Task OtlpEndpointSet(OtlpProtocol? protocol, string? grpcEndpoint, string? httpEndpoint, string expectedUrl, string expectedProtocol)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = grpcEndpoint;
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = httpEndpoint;

        var container = builder.AddResource(new ContainerResource("testSource"));

        if (protocol is { } value)
        {
            container = container.WithOtlpExporter(value);
        }
        else
        {
            container = container.WithOtlpExporter();
        }

        using var app = builder.Build();

        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal(expectedUrl, config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(expectedProtocol, config["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [InlineData(OtlpProtocol.HttpProtobuf)]
    [InlineData(OtlpProtocol.HttpJson)]
    [Theory]
    public async Task RequiredHttpOtlpThrowsExceptionIfNotRegistered(OtlpProtocol protocol)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = null;

        var container = builder.AddResource(new ContainerResource("testSource"))
            .WithOtlpExporter(protocol);

        using var app = builder.Build();

        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                container.Resource,
                serviceProvider: serviceProvider
            ).DefaultTimeout()
        );
    }

    [InlineData(default, "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "otlp-grpc", "http2", 52000, "grpc")]
    [InlineData(OtlpProtocol.HttpProtobuf, "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", "otlp-http", null, 53000, "http/protobuf")]
    [InlineData(OtlpProtocol.HttpJson, "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", "otlp-http", null, 53000, "http/json")]
    [Theory]
    public async Task OtlpEndpointResolvesFromDashboardEndpoint(OtlpProtocol? protocol, string configKey, string endpointName, string? transport, int allocatedPort, string expectedProtocol)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Configure a static OTLP endpoint URL that should be overridden by the dashboard endpoint.
        builder.Configuration[configKey] = "http://localhost:18889";

        // Add a fake dashboard resource with an OTLP endpoint.
        var dashboard = builder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: endpointName, uriScheme: "http", port: 18889, isProxied: true, transport: transport));

        var container = builder.AddResource(new ContainerResource("testSource"));
        if (protocol is { } value)
        {
            container = container.WithOtlpExporter(value);
        }
        else
        {
            container = container.WithOtlpExporter();
        }

        using var app = builder.Build();
        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        // Simulate DCP allocating a different port (e.g. isolated mode with randomized ports).
        var annotation = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == endpointName);
        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", allocatedPort);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal($"http://localhost:{allocatedPort}", config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(expectedProtocol, config["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public async Task OtlpEndpointPrefersGrpcWhenBothEndpointsExist()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:18889";
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:18890";

        var dashboard = builder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: KnownEndpointNames.OtlpGrpcEndpointName, uriScheme: "http", port: 18889, isProxied: true, transport: "http2"));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: KnownEndpointNames.OtlpHttpEndpointName, uriScheme: "http", port: 18890, isProxied: true));

        var container = builder.AddResource(new ContainerResource("testSource"))
            .WithOtlpExporter();

        using var app = builder.Build();
        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        var grpcAnnotation = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        grpcAnnotation.AllocatedEndpoint = new AllocatedEndpoint(grpcAnnotation, "localhost", 52000);

        var httpAnnotation = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == KnownEndpointNames.OtlpHttpEndpointName);
        httpAnnotation.AllocatedEndpoint = new AllocatedEndpoint(httpAnnotation, "localhost", 53000);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal("http://localhost:52000", config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("grpc", config["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public async Task OtlpEndpointFallsBackToConfigWhenNoDashboardResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:18889";

        // No dashboard resource added - should fall back to config.
        var container = builder.AddResource(new ContainerResource("testSource"))
            .WithOtlpExporter();

        using var app = builder.Build();
        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal("http://localhost:18889", config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("grpc", config["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public async Task OtlpEndpointResolvesFromDashboardForExecutableResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:18889";

        var dashboard = builder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: KnownEndpointNames.OtlpGrpcEndpointName, uriScheme: "http", port: 18889, isProxied: true, transport: "http2"));

        var executable = builder.AddResource(new ExecutableResource("testExe", "test.exe", "."))
            .WithOtlpExporter();

        using var app = builder.Build();
        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        // Allocate endpoint with a different port to simulate randomized ports.
        var grpcAnnotation = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        grpcAnnotation.AllocatedEndpoint = new AllocatedEndpoint(grpcAnnotation, "localhost", 52000);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal("http://localhost:52000", config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("grpc", config["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public async Task OtlpEndpointCanBeOverriddenToPointToAnotherResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:18889";

        // Add a dashboard resource with an OTLP endpoint.
        var dashboard = builder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: KnownEndpointNames.OtlpGrpcEndpointName, uriScheme: "http", port: 18889, isProxied: true, transport: "http2"));

        // Add a collector resource with its own OTLP endpoint (similar to the OTel collector sample).
        var collector = builder.AddResource(new ContainerResource("otel-collector"))
            .WithEndpoint(targetPort: 4317, name: "otlp-grpc", scheme: "http");

        // Add a resource that sends telemetry via OTLP.
        var container = builder.AddResource(new ContainerResource("myapp"))
            .WithOtlpExporter();

        // Override the OTLP endpoint to point to the collector, like the aspire-samples pattern:
        // https://github.com/microsoft/aspire-samples/blob/main/samples/Metrics/MetricsApp.AppHost/OpenTelemetryCollector/OpenTelemetryCollectorResourceBuilderExtensions.cs
        builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
        {
            var collectorEndpoint = collector.Resource.GetEndpoint("otlp-grpc");
            var appModel = e.Services.GetRequiredService<DistributedApplicationModel>();

            foreach (var resource in appModel.Resources)
            {
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                {
                    if (context.EnvironmentVariables.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"))
                    {
                        context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = collectorEndpoint;
                    }
                }));
            }

            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate DCP allocating endpoints.
        var dashboardAnnotation = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        dashboardAnnotation.AllocatedEndpoint = new AllocatedEndpoint(dashboardAnnotation, "localhost", 52000);

        var collectorAnnotation = collector.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "otlp-grpc");
        collectorAnnotation.AllocatedEndpoint = new AllocatedEndpoint(collectorAnnotation, "localhost", 4317);

        // Fire BeforeStartEvent so the override annotations are added.
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        // The endpoint should point to the collector, not the dashboard.
        Assert.Equal("http://localhost:4317", config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }
}
