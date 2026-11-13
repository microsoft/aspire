// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class BlazorWebSocketOriginTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(null, "/_blazor")]
    [InlineData("https://evil.example.com", "/_blazor")]
    [InlineData("https://evil.example.com", "/_blazor/")]
    public async Task BlazorWebSocket_InvalidOrigin_ReturnsForbidden(string? origin, string path)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var frontendUri = new Uri(app.FrontendSingleEndPointAccessor().GetResolvedAddress());
        using var client = new HttpClient { BaseAddress = frontendUri };
        using var request = CreateWebSocketUpgradeRequest(origin, path);
        using var response = await client.SendAsync(request).DefaultTimeout();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Origin not allowed.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BlazorWebSocket_SameOrigin_UpgradeSucceeds()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var frontendUri = new Uri(app.FrontendSingleEndPointAccessor().GetResolvedAddress());
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", frontendUri.GetLeftPart(UriPartial.Authority));

        await client.ConnectAsync(CreateWebSocketUri(frontendUri), CancellationToken.None).DefaultTimeout();

        Assert.Equal(WebSocketState.Open, client.State);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", CancellationToken.None).DefaultTimeout();
    }

    private static HttpRequestMessage CreateWebSocketUpgradeRequest(string? origin, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    private static Uri CreateWebSocketUri(Uri frontendUri)
    {
        return new UriBuilder(frontendUri)
        {
            Scheme = frontendUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/_blazor"
        }.Uri;
    }
}