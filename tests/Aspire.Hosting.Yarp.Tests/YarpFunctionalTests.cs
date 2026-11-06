// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Yarp.Transforms;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Aspire.Hosting.Yarp.Tests;

public class YarpFunctionalTests(ITestOutputHelper testOutputHelper)
{
    // The floating aspnetapp tag can move to a platform-specific manifest; pin
    // this functional test to a multi-platform tag because it runs on Linux CI.
    private const string BackendImage = "mcr.microsoft.com/dotnet/samples:aspnetapp-8.0";

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/17020")]
    public async Task VerifyYarpResourceExtensionsConfig()
    {
        await VerifyYarpResource((yarp, endpoint) =>
        {
            yarp.WithConfiguration(builder =>
            {
                builder.AddRoute("/aspnetapp/{**catch-all}", endpoint)
                       .WithTransformPathRemovePrefix("/aspnetapp");
            });
        });
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task StaticFilesServeSpaFallbackWithExpectedProxyRoutePriority()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var frontendPath = Path.Combine(workspace.Path, "frontend");
        var backendPath = Path.Combine(workspace.Path, "backend");
        Directory.CreateDirectory(frontendPath);
        Directory.CreateDirectory(backendPath);
        await File.WriteAllTextAsync(Path.Combine(frontendPath, "index.html"), "<html>SPA index</html>");
        await File.WriteAllTextAsync(Path.Combine(frontendPath, "app.js"), "console.log('app');");
        await File.WriteAllTextAsync(Path.Combine(frontendPath, "plain-file"), "static file");
        await File.WriteAllTextAsync(Path.Combine(backendPath, "api.txt"), "backend response");

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var backend = builder.AddYarp("backend")
            .WithStaticFiles(backendPath)
            .WithHttpHealthCheck("/api.txt", endpointName: "http");

        var frontend = builder.AddYarp("frontend")
            .WithEndpoint("http", endpoint => endpoint.TargetPort = 6200)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:6200")
            .WithStaticFiles(frontendPath)
            .WithSpaFallback()
            .WithEnvironment("ReverseProxy__Clusters__aspireSpaFallback__Destinations__external__Address", backend.GetEndpoint("http"))
            .WithEnvironment("ReverseProxy__Routes__external__ClusterId", "aspireSpaFallback")
            .WithEnvironment("ReverseProxy__Routes__external__Match__Path", "/external/{**catch-all}")
            .WithEnvironment("ReverseProxy__Routes__external__Transforms__0__PathRemovePrefix", "/external")
            .WithConfiguration(config =>
            {
                config.AddRoute("/api/{**catch-all}", backend.GetEndpoint("http"))
                    .WithTransformPathRemovePrefix("/api")
                    .WithOrder(int.MaxValue);
            })
            .WithHttpHealthCheck("/index.html", endpointName: "http");

        var catchAll = builder.AddYarp("catchall")
            .WithStaticFiles(frontendPath)
            .WithSpaFallback()
            .WithConfiguration(config => config.AddRoute("/{**catch-all}", backend.GetEndpoint("http")))
            .WithHttpHealthCheck("/index.html", endpointName: "http");

        var fallbackOrderCatchAll = builder.AddYarp("fallbackorder")
            .WithStaticFiles(frontendPath)
            .WithSpaFallback()
            .WithConfiguration(config => config.AddRoute("/{**catch-all}", backend.GetEndpoint("http")).WithOrder(int.MaxValue))
            .WithHttpHealthCheck("/index.html", endpointName: "http");

        using var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startTimeout.Token);

        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Task.WhenAll(
            app.ResourceNotifications.WaitForResourceHealthyAsync(backend.Resource.Name, readyTimeout.Token),
            app.ResourceNotifications.WaitForResourceHealthyAsync(frontend.Resource.Name, readyTimeout.Token),
            app.ResourceNotifications.WaitForResourceHealthyAsync(catchAll.Resource.Name, readyTimeout.Token),
            app.ResourceNotifications.WaitForResourceHealthyAsync(fallbackOrderCatchAll.Resource.Name, readyTimeout.Token));

        using var client = app.CreateHttpClient(frontend.Resource.Name, "http");
        client.Timeout = TimeSpan.FromSeconds(15);

        Assert.Equal("<html>SPA index</html>", await client.GetStringAsync("/"));
        Assert.Equal("<html>SPA index</html>", await client.GetStringAsync("/test"));
        Assert.Equal("<html>SPA index</html>", await client.GetStringAsync("/test/nested?query=value"));
        Assert.Equal("console.log('app');", await client.GetStringAsync("/app.js"));
        // The image does not serve files without a recognized content type.
        Assert.Equal("<html>SPA index</html>", await client.GetStringAsync("/plain-file"));

        using var missingAsset = await client.GetAsync("/missing.js");
        Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/test");
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(string.Empty, await headResponse.Content.ReadAsStringAsync());

        using var postResponse = await client.PostAsync("/test", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);

        Assert.Equal("backend response", await client.GetStringAsync("/api/api.txt"));
        Assert.Equal("backend response", await client.GetStringAsync("/external/api.txt"));
        using var missingApi = await client.GetAsync("/api/missing");
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);

        using var backendClient = app.CreateHttpClient(backend.Resource.Name, "http");
        backendClient.Timeout = TimeSpan.FromSeconds(15);
        using var defaultResponse = await backendClient.GetAsync("/test");
        Assert.Equal(HttpStatusCode.NotFound, defaultResponse.StatusCode);

        using var catchAllClient = app.CreateHttpClient(catchAll.Resource.Name, "http");
        catchAllClient.Timeout = TimeSpan.FromSeconds(15);
        Assert.Equal("backend response", await catchAllClient.GetStringAsync("/api.txt"));
        using var missingCatchAll = await catchAllClient.GetAsync("/test");
        Assert.Equal(HttpStatusCode.NotFound, missingCatchAll.StatusCode);

        using var fallbackOrderClient = app.CreateHttpClient(fallbackOrderCatchAll.Resource.Name, "http");
        fallbackOrderClient.Timeout = TimeSpan.FromSeconds(15);
        Assert.Equal("backend response", await fallbackOrderClient.GetStringAsync("/api.txt"));
        Assert.Equal("<html>SPA index</html>", await fallbackOrderClient.GetStringAsync("/test"));

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task SpaFallbackPreservesTheOriginalHostForHostFiltering()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "index.html"), "<html>SPA index</html>");

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        // Health probes use localhost, which the restricted host configuration rejects.
        var yarp = builder.AddYarp("restricted")
            .WithEnvironment("AllowedHosts", "example.com")
            .WithStaticFiles(workspace.Path)
            .WithSpaFallback()
            .WithHttpHealthCheck("/index.html", statusCode: (int)HttpStatusCode.BadRequest, endpointName: "http");

        using var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startTimeout.Token);

        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(yarp.Resource.Name, readyTimeout.Token);

        using var client = app.CreateHttpClient(yarp.Resource.Name, "http");
        client.Timeout = TimeSpan.FromSeconds(15);

        using var indexRequest = new HttpRequestMessage(HttpMethod.Get, "/index.html");
        indexRequest.Headers.Host = "example.com";
        using var indexResponse = await client.SendAsync(indexRequest);
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Equal("<html>SPA index</html>", await indexResponse.Content.ReadAsStringAsync());

        using var navigationRequest = new HttpRequestMessage(HttpMethod.Get, "/nested/page");
        navigationRequest.Headers.Host = "example.com";
        using var navigationResponse = await client.SendAsync(navigationRequest);
        Assert.Equal(HttpStatusCode.OK, navigationResponse.StatusCode);
        Assert.Equal("<html>SPA index</html>", await navigationResponse.Content.ReadAsStringAsync());

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/nested/page");
        headRequest.Headers.Host = "example.com";
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(string.Empty, await headResponse.Content.ReadAsStringAsync());

        using var blockedRequest = new HttpRequestMessage(HttpMethod.Get, "/nested/page");
        blockedRequest.Headers.Host = "not-allowed.test";
        using var blockedResponse = await client.SendAsync(blockedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);

        await app.StopAsync();
    }

    [Theory]
    [InlineData(true, "Aspire fallback")]
    [InlineData(false, "native fallback")]
    public async Task GeneratedFallbackCanCoexistWithNativeFallback(bool enableAspireFallback, string expectedResponse)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "index.html"), "native fallback");

        var backendBuilder = WebApplication.CreateSlimBuilder();
        backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var backend = backendBuilder.Build();
        backend.MapGet("/index.html", () => "Aspire fallback");
        await backend.StartAsync();

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var yarp = builder.AddYarp("yarp").WithStaticFiles().WithSpaFallback(enableAspireFallback);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        var proxySettings = environment
            .Where(item => item.Key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key.Replace("__", ":"), item => (string?)item.Value);
        if (enableAspireFallback)
        {
            proxySettings["REVERSEPROXY:CLUSTERS:aspireSpaFallback:DESTINATIONS:self:ADDRESS"] = backend.Urls.Single();
        }

        var proxyBuilder = WebApplication.CreateSlimBuilder();
        proxyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        proxyBuilder.Configuration.AddInMemoryCollection(proxySettings);
        proxyBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        proxyBuilder.Services.AddReverseProxy().LoadFromConfig(proxyBuilder.Configuration.GetSection("REVERSEPROXY"));
        using var files = new PhysicalFileProvider(workspace.Path);
        await using var proxy = proxyBuilder.Build();
        proxy.MapReverseProxy();
        proxy.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
        await proxy.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(proxy.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync("/test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedResponse, await response.Content.ReadAsStringAsync());
    }

    private async Task VerifyYarpResource(Action<IResourceBuilder<YarpResource>, EndpointReference> configurator)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var backend = builder
            .AddContainer("backend", BackendImage)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        var yarp = builder.AddYarp("yarp");

        configurator(yarp, backend.GetEndpoint("http"));

        yarp.WithHttpHealthCheck("/heath", 404); // TODO we don't have real health check path yet

        var app = builder.Build();

        await app.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(backend.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(yarp.Resource.Name, cts.Token);

        using var httpClient = app.CreateHttpClient(yarp.Resource.Name);

        using var response200 = await httpClient.GetAsync("/aspnetapp");
        Assert.Equal(System.Net.HttpStatusCode.OK, response200.StatusCode);

        using var response404 = await httpClient.GetAsync("/another");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response404.StatusCode);

        await app.StopAsync();
    }
}
