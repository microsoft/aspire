// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;

namespace Aspire.Hosting.Tests.ApplicationModel;

[Trait("Partition", "4")]
public class McpServerEndpointAnnotationTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenResolverIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new McpServerEndpointAnnotation(null!));
    }

    [Fact]
    public async Task Constructor_WithCustomResolver_UsesProvidedResolver()
    {
        var expectedUri = new Uri("http://custom.example.com:9000/mcp");
        var annotation = new McpServerEndpointAnnotation((resource, ct) => Task.FromResult<Uri?>(expectedUri));

        var resource = new TestResourceWithEndpoints("test");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Equal(expectedUri, result);
    }

    [Fact]
    public async Task Constructor_WithCustomResolver_CanReturnNull()
    {
        var annotation = new McpServerEndpointAnnotation((resource, ct) => Task.FromResult<Uri?>(null));

        var resource = new TestResourceWithEndpoints("test");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void FromEndpoint_ThrowsArgumentNullException_WhenEndpointNameIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => McpServerEndpointAnnotation.FromEndpoint(null!));
    }

    [Fact]
    public void FromEndpoint_ThrowsArgumentException_WhenEndpointNameIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => McpServerEndpointAnnotation.FromEndpoint(""));
    }

    [Fact]
    public void FromEndpoint_ThrowsArgumentException_WhenEndpointNameIsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => McpServerEndpointAnnotation.FromEndpoint("   "));
    }

    [Fact]
    public async Task FromEndpoint_ReturnsNull_WhenEndpointDoesNotExist()
    {
        var annotation = McpServerEndpointAnnotation.FromEndpoint("nonexistent");

        var resource = new TestResourceWithEndpoints("test");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FromEndpoint_ReturnsUrlWithDefaultPath_WhenEndpointExists()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/mcp", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_ReturnsUrlWithCustomPath_WhenPathSpecified()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http", "/sse");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/sse", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_ReturnsBaseUrl_WhenPathIsNull()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http", null);
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        // Uri normalizes to include trailing slash for absolute URIs without path
        Assert.Equal("http://localhost:8080/", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_ReturnsBaseUrl_WhenPathIsEmpty()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http", "");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        // Uri normalizes to include trailing slash for absolute URIs without path
        Assert.Equal("http://localhost:8080/", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_NormalizesPathWithoutLeadingSlash()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http", "custom/path");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/custom/path", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_HandlesTrailingSlashInBaseUrl()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http", "/mcp");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/mcp", result.ToString());
    }

    [Fact]
    public async Task FromEndpoint_WaitsForEndpointAllocation()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        // Not setting AllocatedEndpoint initially

        var annotation = McpServerEndpointAnnotation.FromEndpoint("http");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // The resolver will wait for allocation, so we expect it to be cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => annotation.EndpointUrlResolver(resource, cts.Token));
    }

    [Fact]
    public async Task FromEndpoint_WorksWithHttpsEndpoint()
    {
        var resource = new TestResourceWithEndpoints("test");
        var endpointAnnotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8443);

        var annotation = McpServerEndpointAnnotation.FromEndpoint("https", "/mcp");
        var result = await annotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://localhost:8443/mcp", result.ToString());
    }

    private sealed class TestResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints
    {
    }
}
