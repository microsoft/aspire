// <copyright file="AspireChaosClientExtensionsTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

#pragma warning disable ASPIRECHAOS001 // chaos packages are experimental at this milestone

using Aspire.Chaos.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Verifies the Aspire-shaped client integration: <c>AddChaosProxyClient</c> resolves
/// the connection string from the standard <c>ConnectionStrings</c> section, configures
/// a typed <see cref="ChaosProxyClient"/> with the right <see cref="HttpClient.BaseAddress"/>,
/// registers a health check by default, and respects <see cref="ChaosProxyClientSettings"/>
/// overrides.
/// </summary>
public class AspireChaosClientExtensionsTests
{
    [Fact]
    public void AddChaosProxyClient_FromConnectionString_RegistersTypedHttpClient()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:chaos-be"] = "http://localhost:7777";

        builder.AddChaosProxyClient("chaos-be");

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<ChaosProxyClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddChaosProxyClient_NoConnectionStringOrEndpoint_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.AddChaosProxyClient("missing-connection"));
    }

    [Fact]
    public void AddChaosProxyClient_EndpointSetViaConfigureSettings_Wins()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddChaosProxyClient("chaos-be", settings =>
        {
            settings.Endpoint = new Uri("http://override:9000");
        });

        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(ChaosProxyClient));
        Assert.Equal("http://override:9000/", http.BaseAddress?.ToString());
    }

    [Fact]
    public void AddChaosProxyClient_RegistersHealthCheckByDefault()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:chaos-be"] = "http://localhost:7777";
        builder.AddChaosProxyClient("chaos-be");

        using var host = builder.Build();
        var hcService = host.Services.GetService<HealthCheckService>();
        Assert.NotNull(hcService);
    }

    [Fact]
    public void AddChaosProxyClient_DisableHealthChecks_SkipsRegistration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:chaos-be"] = "http://localhost:7777";
        builder.AddChaosProxyClient("chaos-be", settings => settings.DisableHealthChecks = true);

        using var host = builder.Build();
        // HealthCheckService is registered only when at least one HealthCheck is registered.
        // We don't have an easy way to assert "no chaos-proxy health check" via the public
        // surface, but we can assert that no HealthCheckService is present (since we registered
        // no other health checks).
        var hcService = host.Services.GetService<HealthCheckService>();
        Assert.Null(hcService);
    }

    [Fact]
    public void AddKeyedChaosProxyClient_RegistersUnderServiceKey()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:chaos-a"] = "http://chaos-a:7777";
        builder.Configuration["ConnectionStrings:chaos-b"] = "http://chaos-b:7778";

        builder.AddKeyedChaosProxyClient("chaos-a");
        builder.AddKeyedChaosProxyClient("chaos-b");

        using var host = builder.Build();
        var a = host.Services.GetRequiredKeyedService<ChaosProxyClient>("chaos-a");
        var b = host.Services.GetRequiredKeyedService<ChaosProxyClient>("chaos-b");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddChaosProxyClient_ConfigureHttpClient_Invoked()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:chaos-be"] = "http://localhost:7777";
        var invoked = false;

        builder.AddChaosProxyClient(
            "chaos-be",
            configureHttpClient: clientBuilder => invoked = true);

        Assert.True(invoked);
    }

    [Fact]
    public void AddChaosProxyClient_NullBuilder_Throws()
    {
        IHostApplicationBuilder builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.AddChaosProxyClient("chaos-be"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AddChaosProxyClient_EmptyConnectionName_Throws(string? connectionName)
    {
        var builder = Host.CreateApplicationBuilder();
        Assert.ThrowsAny<ArgumentException>(() => builder.AddChaosProxyClient(connectionName!));
    }
}
