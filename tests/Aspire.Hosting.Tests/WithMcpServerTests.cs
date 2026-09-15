// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREMCP001

using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithMcpServerTests
{
    [Fact]
    public void WithMcpServer_ThrowsArgumentNullException_WhenBuilderIsNull()
    {
        IResourceBuilder<ContainerResource> builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.WithMcpServer());
    }

    [Fact]
    public async Task WithMcpServer_AddsMcpServerEndpointAnnotation()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http")
            .WithMcpServer(endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());

        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());
        Assert.NotNull(mcpAnnotation.EndpointUrlResolver);
    }

    [Fact]
    public async Task WithMcpServer_DefaultsToHttpsEndpoint()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("https", e =>
            {
                e.UriScheme = "https";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8443);
            })
            .WithHttpEndpoint(name: "http")
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("https://localhost:8443/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_FallsBackToHttpEndpoint()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesDefaultMcpPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer(endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesCustomPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer("/sse", endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/sse", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesNullPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer(path: null, endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        // Uri normalizes to include trailing slash for absolute URIs without path
        Assert.Equal("http://localhost:8080/", resolvedUri!.ToString());
    }

    [Fact]
    public void WithMcpServer_ReturnsBuilderForChaining()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var container = appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http");

        var result = container.WithMcpServer(endpointName: "http");

        Assert.Same(container, result);
    }
}
