// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class BlazorHostedExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ProxyService_EmitsYarpRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.Equal("cluster-weatherapi", env["ReverseProxy__Routes__route-weatherapi__ClusterId"]);
        Assert.Equal("/_api/weatherapi/{**catch-all}", env["ReverseProxy__Routes__route-weatherapi__Match__Path"]);
        Assert.Equal("/_api/weatherapi", env["ReverseProxy__Routes__route-weatherapi__Transforms__0__PathRemovePrefix"]);
        Assert.Equal("https+http://weatherapi", env["ReverseProxy__Clusters__cluster-weatherapi__Destinations__d1__Address"]);
    }

    [Fact]
    public async Task ProxyService_EmitsClientConfigResponse()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .ProxyService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.True(env.ContainsKey("Client__ConfigResponse"));
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("services__weatherapi__http__0", configJson);
        Assert.Contains("/_api/weatherapi", configJson);
        Assert.Equal("/_blazor/_configuration", env["Client__ConfigEndpointPath"]);
    }

    [Fact]
    public async Task ProxyTelemetry_EmitsOtlpRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        Assert.Equal("cluster-otlp-dashboard", env["ReverseProxy__Routes__route-otlp__ClusterId"]);
        Assert.Equal("/_otlp/{**catch-all}", env["ReverseProxy__Routes__route-otlp__Match__Path"]);
        Assert.Equal("/_otlp", env["ReverseProxy__Routes__route-otlp__Transforms__0__PathRemovePrefix"]);
    }

    [Fact]
    public async Task ProxyTelemetry_EmitsOtelServiceNameInConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("OTEL_SERVICE_NAME", configJson);
        Assert.Contains("blazorapp", configJson);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
        Assert.Contains("/_otlp/", configJson);
    }

    [Fact]
    public async Task ProxyService_And_ProxyTelemetry_Combined()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyService(weatherApi)
            .ProxyTelemetry();

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Service routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-weatherapi__ClusterId"));

        // OTLP routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-otlp__ClusterId"));

        // Config response includes both service URLs and OTLP
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
        Assert.Contains("OTEL_SERVICE_NAME", configJson);
    }

    [Fact]
    public async Task ProxyService_MultipleServices()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");
        var catalogApi = builder.AddProject<TestProjectMetadata>("catalogapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyService(weatherApi)
            .ProxyService(catalogApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Both services have routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-weatherapi__ClusterId"));
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-catalogapi__ClusterId"));

        // Config response includes both services
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("services__weatherapi__https__0", configJson);
        Assert.Contains("services__catalogapi__https__0", configJson);
    }

    [Fact]
    public async Task ProxyService_NoPathPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Hosted mode uses no path prefix — URLs are relative to root
        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.Contains("/_api/weatherapi", configJson);
        Assert.DoesNotContain("/blazorapp/", configJson);
    }

    [Fact]
    public async Task ProxyService_WithoutProxyTelemetry_NoOtlpInConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi");

        builder.AddProject<TestProjectMetadata>("blazorapp")
            .WithHttpsEndpoint()
            .ProxyService(weatherApi);

        var blazorApp = builder.Resources.Single(r => r.Name == "blazorapp");
        var env = await GetEnvironmentVariables(blazorApp, builder);

        // Without ProxyTelemetry, no OTLP routes or config
        Assert.False(env.ContainsKey("ReverseProxy__Routes__route-otlp__ClusterId"));

        var configJson = ResolveManifestExpression(env["Client__ConfigResponse"]);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", configJson);
    }

    private static async Task<Dictionary<string, object>> GetEnvironmentVariables(
        IResource resource, IDistributedApplicationBuilder builder)
    {
        var env = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, env);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context).ConfigureAwait(false);
        }
        return env;
    }

    private static string ResolveManifestExpression(object value)
    {
        if (value is IManifestExpressionProvider provider)
        {
            return provider.ValueExpression;
        }
        return (string)value;
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
