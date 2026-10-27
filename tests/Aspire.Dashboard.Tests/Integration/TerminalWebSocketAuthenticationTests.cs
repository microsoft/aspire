// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.WebSockets;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class TerminalWebSocketAuthenticationTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(FrontendAuthMode.BrowserToken, "/api/terminal?resource=test")]
    [InlineData(FrontendAuthMode.BrowserToken, "/api/apphost-terminal?terminalId=test")]
    [InlineData(FrontendAuthMode.OpenIdConnect, "/api/terminal?resource=test")]
    [InlineData(FrontendAuthMode.OpenIdConnect, "/api/apphost-terminal?terminalId=test")]
    public async Task TerminalWebSocket_Unauthenticated_RejectsUpgradeWithAuthenticationChallenge(FrontendAuthMode authMode, string endpoint)
    {
        await using var authority = authMode is FrontendAuthMode.OpenIdConnect
            ? await MockOpenIdAuthority.CreateAsync().DefaultTimeout()
            : null;
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = authMode.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "test-token";
            if (authority is not null)
            {
                config["Authentication:Schemes:OpenIdConnect:Authority"] = authority.Url;
                config["Authentication:Schemes:OpenIdConnect:ClientId"] = "MyClientId";
                config["Authentication:Schemes:OpenIdConnect:ClientSecret"] = "MyClientSecret";
            }
        });
        await app.StartAsync().DefaultTimeout();

        var frontendUri = new Uri(app.FrontendSingleEndPointAccessor().GetResolvedAddress());
        var terminalUri = new Uri(frontendUri, endpoint);
        var websocketUri = new UriBuilder(terminalUri)
        {
            Scheme = "ws"
        }.Uri;
        // Do not follow the authentication redirect: a failure on the login page
        // would not prove that the terminal endpoint itself rejected the upgrade.
        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        // A valid Origin ensures authentication, rather than the CSWSH defense,
        // is what prevents this cookie-less client from reaching the terminal.
        socket.Options.SetRequestHeader("Origin", frontendUri.GetLeftPart(UriPartial.Authority));

        var exception = await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(websocketUri, invoker, CancellationToken.None).DefaultTimeout());

        Assert.Equal(WebSocketError.NotAWebSocket, exception.WebSocketErrorCode);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(HttpStatusCode.Redirect, socket.HttpStatusCode);
        Assert.NotNull(socket.HttpResponseHeaders);
        var location = new Uri(Assert.Single(socket.HttpResponseHeaders["Location"]));
        if (authority is not null)
        {
            Assert.Equal(new Uri(authority.Url).GetLeftPart(UriPartial.Authority), location.GetLeftPart(UriPartial.Authority));
            Assert.Equal("/authorize", location.AbsolutePath);
        }
        else
        {
            Assert.Equal(frontendUri.GetLeftPart(UriPartial.Authority), location.GetLeftPart(UriPartial.Authority));
            Assert.Equal("/login", location.AbsolutePath);
        }
    }
}
