// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    [Fact]
    public async Task RequiredHttpOtlpThrowsExceptionIfNotRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = null;

        var container = builder.AddResource(new ContainerResource("testSource"))
            .WithOtlpExporter(OtlpProtocol.HttpProtobuf);

        using var app = builder.Build();

        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                container.Resource,
                serviceProvider: serviceProvider
            ).DefaultTimeout()
        );
    }

    [Fact]
    public async Task RequiredHttpJsonOtlpThrowsExceptionIfNotRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = null;

        var container = builder.AddResource(new ContainerResource("testSource"))
            .WithOtlpExporter(OtlpProtocol.HttpJson);

        using var app = builder.Build();

        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                container.Resource,
                serviceProvider: serviceProvider
            ).DefaultTimeout()
        );
    }
}
