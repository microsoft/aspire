// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Authentication.Connection;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class BrowserSecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_Development_AllowExternalFetch()
    {
        // Arrange
        var middleware = CreateMiddleware(environmentName: "Development");
        var httpContext = new DefaultHttpContext();

        // Act
        await middleware.InvokeAsync(httpContext).DefaultTimeout();

        // Assert
        Assert.NotEqual(StringValues.Empty, httpContext.Response.Headers.ContentSecurityPolicy);
        Assert.DoesNotContain("default-src", httpContext.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task InvokeAsync_Production_DenyExternalFetch()
    {
        // Arrange
        var middleware = CreateMiddleware(environmentName: "Production");
        var httpContext = new DefaultHttpContext();

        // Act
        await middleware.InvokeAsync(httpContext).DefaultTimeout();

        // Assert
        Assert.NotEqual(StringValues.Empty, httpContext.Response.Headers.ContentSecurityPolicy);
        Assert.Contains("default-src", httpContext.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Theory]
    [InlineData("https", "img-src data: https:;")]
    [InlineData("http", "img-src data: http: https:;")]
    public async Task InvokeAsync_Scheme_ImageSourceChangesOnScheme(string scheme, string expectedContent)
    {
        // Arrange
        var middleware = CreateMiddleware(environmentName: "Production");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;

        // Act
        await middleware.InvokeAsync(httpContext).DefaultTimeout();

        // Assert
        Assert.NotEqual(StringValues.Empty, httpContext.Response.Headers.ContentSecurityPolicy);
        Assert.Contains(expectedContent, httpContext.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Theory]
    [InlineData(ConnectionType.OtlpGrpc)]
    [InlineData(ConnectionType.OtlpHttp)]
    public async Task InvokeAsync_Otlp_NotAdded(ConnectionType connectionType)
    {
        // Arrange
        var middleware = CreateMiddleware(environmentName: "Production");
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IConnectionTypeFeature>(new TestConnectionTypeFeature { ConnectionTypes = [connectionType] });

        // Act
        await middleware.InvokeAsync(httpContext).DefaultTimeout();

        // Assert
        Assert.Equal(StringValues.Empty, httpContext.Response.Headers.ContentSecurityPolicy);
    }

    private sealed class TestConnectionTypeFeature : IConnectionTypeFeature
    {
        public required List<ConnectionType> ConnectionTypes { get; init; }
    }

    private static BrowserSecurityHeadersMiddleware CreateMiddleware(string environmentName) =>
        new BrowserSecurityHeadersMiddleware(c => Task.CompletedTask, new TestHostEnvironment { EnvironmentName = environmentName });

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "ApplicationName";
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = "ContentRootPath";
        public string EnvironmentName { get; set; } = "Development";
    }
}
