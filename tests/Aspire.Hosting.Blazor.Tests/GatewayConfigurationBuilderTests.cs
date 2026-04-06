// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Blazor.Tests;

public class GatewayConfigurationBuilderTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void EmitProxyConfiguration_EmitsYarpRoutes_ForServices()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        // Verify YARP route config
        Assert.Equal("cluster-weatherapi", env["ReverseProxy__Routes__route-store-weatherapi__ClusterId"]);
        Assert.Equal("/store/_api/weatherapi/{**catch-all}", env["ReverseProxy__Routes__route-store-weatherapi__Match__Path"]);
        Assert.Equal("/store/_api/weatherapi", env["ReverseProxy__Routes__route-store-weatherapi__Transforms__0__PathRemovePrefix"]);
    }

    [Fact]
    public void EmitProxyConfiguration_EmitsYarpCluster_ForService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        Assert.Equal("https+http://weatherapi", env["ReverseProxy__Clusters__cluster-weatherapi__Destinations__d1__Address"]);
    }

    [Fact]
    public void EmitProxyConfiguration_EmitsOtlpProxy_ForApp()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", []);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        Assert.Equal("cluster-otlp-dashboard", env["ReverseProxy__Routes__route-otlp-store__ClusterId"]);
        Assert.Equal("/store/_otlp/{**catch-all}", env["ReverseProxy__Routes__route-otlp-store__Match__Path"]);
        Assert.Equal("/store/_otlp", env["ReverseProxy__Routes__route-otlp-store__Transforms__0__PathRemovePrefix"]);
    }

    [Fact]
    public void EmitProxyConfiguration_SharedCluster_NotDuplicated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var storeApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        var adminApp = builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        var apps = new List<GatewayAppRegistration>
        {
            new(storeApp, "store", ["weatherapi"]),
            new(adminApp, "admin", ["weatherapi"])
        };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        // Both apps reference weatherapi, but cluster should only be defined once
        Assert.Equal("https+http://weatherapi", env["ReverseProxy__Clusters__cluster-weatherapi__Destinations__d1__Address"]);

        // Both apps should have their own routes
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-store-weatherapi__ClusterId"));
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-admin-weatherapi__ClusterId"));
    }

    [Fact]
    public void EmitProxyConfiguration_MultipleServices_AllRoutesEmitted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi", "catalogapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-store-weatherapi__ClusterId"));
        Assert.True(env.ContainsKey("ReverseProxy__Routes__route-store-catalogapi__ClusterId"));
        Assert.True(env.ContainsKey("ReverseProxy__Clusters__cluster-weatherapi__Destinations__d1__Address"));
        Assert.True(env.ContainsKey("ReverseProxy__Clusters__cluster-catalogapi__Destinations__d1__Address"));
    }

    [Fact]
    public void EmitProxyConfiguration_OtlpHeaders_TransformedToRequestHeaders()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", []);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>
        {
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=abc123"
        };
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        Assert.Equal("x-otlp-api-key", env["ReverseProxy__Routes__route-otlp-store__Transforms__1__RequestHeader"]);
        Assert.Equal("abc123", env["ReverseProxy__Routes__route-otlp-store__Transforms__1__Set"]);
    }

    [Fact]
    public void EmitProxyConfiguration_ForwardsOtlpEndpoint_AsOtlpCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", []);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317"
        };
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        Assert.Equal("http://localhost:4317", env["ReverseProxy__Clusters__cluster-otlp-dashboard__Destinations__d1__Address"]);
    }

    [Fact]
    public void EmitProxyConfiguration_EmitsClientConfigResponse()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");
        var httpGatewayEndpoint = gateway.GetEndpoint("http");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint, httpGatewayEndpoint);

        // The config response is an IValueProvider/IManifestExpressionProvider
        Assert.True(env.ContainsKey("ClientApps__store__ConfigResponse"));
        var configResponse = env["ClientApps__store__ConfigResponse"];
        Assert.IsAssignableFrom<IValueProvider>(configResponse);
        Assert.IsAssignableFrom<IManifestExpressionProvider>(configResponse);
    }

    [Fact]
    public void EmitProxyConfiguration_ClientConfig_ContainsHttpAndHttpsServiceUrls()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gateway.GetEndpoint("https"), gateway.GetEndpoint("http"));

        var configResponse = (IManifestExpressionProvider)env["ClientApps__store__ConfigResponse"];
        var manifestExpression = configResponse.ValueExpression;

        Assert.Contains("services__weatherapi__https__0", manifestExpression);
        Assert.Contains("services__weatherapi__http__0", manifestExpression);
    }

    [Fact]
    public void EmitProxyConfiguration_DoesNotEmitOtlpProxy_WhenTelemetryDisabled()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", [], ProxyTelemetry: false);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gateway.GetEndpoint("https"), gateway.GetEndpoint("http"));

        Assert.DoesNotContain("ReverseProxy__Routes__route-otlp-store__ClusterId", env.Keys);

        var configResponse = (IManifestExpressionProvider)env["ClientApps__store__ConfigResponse"];
        var manifestExpression = configResponse.ValueExpression;
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", manifestExpression);
    }

    [Fact]
    public void EmitProxyConfiguration_ClientConfig_IncludesOtelServiceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"]);
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        var configResponse = (IManifestExpressionProvider)env["ClientApps__store__ConfigResponse"];
        var manifestExpression = configResponse.ValueExpression;

        // The config response should contain OTEL_SERVICE_NAME with the resource name
        Assert.Contains("OTEL_SERVICE_NAME", manifestExpression);
        Assert.Contains("store", manifestExpression);
    }

    [Fact]
    public void EmitProxyConfiguration_UsesCustomPrefixes_WhenSpecified()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var gateway = builder.AddProject<TestProjectMetadata>("gateway")
            .WithHttpsEndpoint();

        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");

        var registration = new GatewayAppRegistration(wasmApp, "store", ["weatherapi"], ApiPrefix: "myapi", OtlpPrefix: "myotlp");
        var apps = new List<GatewayAppRegistration> { registration };
        var env = new Dictionary<string, object>();
        var gatewayEndpoint = gateway.GetEndpoint("https");

        GatewayConfigurationBuilder.EmitProxyConfiguration(env, apps, gatewayEndpoint);

        // Verify custom API prefix in YARP route
        Assert.Equal("/store/myapi/weatherapi/{**catch-all}", env["ReverseProxy__Routes__route-store-weatherapi__Match__Path"]);
        Assert.Equal("/store/myapi/weatherapi", env["ReverseProxy__Routes__route-store-weatherapi__Transforms__0__PathRemovePrefix"]);

        // Verify custom OTLP prefix in YARP route
        Assert.Equal("/store/myotlp/{**catch-all}", env["ReverseProxy__Routes__route-otlp-store__Match__Path"]);
        Assert.Equal("/store/myotlp", env["ReverseProxy__Routes__route-otlp-store__Transforms__0__PathRemovePrefix"]);
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
