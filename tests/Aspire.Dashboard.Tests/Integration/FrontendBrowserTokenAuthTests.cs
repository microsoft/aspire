// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Json;
using System.Web;
using Aspire.Dashboard.Authentication;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class FrontendBrowserTokenAuthTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public FrontendBrowserTokenAuthTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task Get_Unauthenticated_RedirectToLogin()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync("/").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DashboardUrls.LoginUrl(returnUrl: DashboardUrls.StructuredLogsUrl()), response.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Get_LoginPage_ValidToken_RedirectToApp()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act 1
        var response1 = await client.GetAsync(DashboardUrls.LoginUrl(returnUrl: DashboardUrls.TracesUrl(), token: apiKey)).DefaultTimeout();

        // Assert 1
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(DashboardUrls.TracesUrl(), response1.RequestMessage!.RequestUri!.PathAndQuery);

        // Act 2
        var response2 = await client.GetAsync(DashboardUrls.StructuredLogsUrl()).DefaultTimeout();

        // Assert 2
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(DashboardUrls.StructuredLogsUrl(), response2.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Get_LoginPage_ValidToken_OtlpHttpConnection_Denied()
    {
        // Arrange
        var testSink = new TestSink();

        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = apiKey;
        }, testSink: testSink);
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync(DashboardUrls.LoginUrl(returnUrl: DashboardUrls.TracesUrl(), token: apiKey)).DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var log = testSink.Writes.Single(s => s.LoggerName == typeof(FrontendCompositeAuthenticationHandler).FullName && s.EventId.Name == "AuthenticationSchemeNotAuthenticatedWithFailure");
        Assert.Equal("FrontendComposite was not authenticated. Failure message: Connection types 'Frontend' are not enabled on this connection.", log.Message);
    }

    [Fact]
    public async Task Get_LoginPage_InvalidToken_RedirectToLoginWithoutToken()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync(DashboardUrls.LoginUrl(returnUrl: DashboardUrls.TracesUrl(), token: "Wrong!")).DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DashboardUrls.LoginUrl(returnUrl: DashboardUrls.TracesUrl()), response.RequestMessage!.RequestUri!.PathAndQuery, ignoreCase: true);
    }

    [Theory]
    [InlineData(FrontendAuthMode.BrowserToken, "TestKey123!", HttpStatusCode.OK, true)]
    [InlineData(FrontendAuthMode.BrowserToken, "Wrong!", HttpStatusCode.OK, false)]
    [InlineData(FrontendAuthMode.Unsecured, "Wrong!", HttpStatusCode.NotFound, null)]
    public async Task Post_ValidateTokenApi_AvailableBasedOnOptions(FrontendAuthMode authMode, string requestToken, HttpStatusCode statusCode, bool? result)
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = authMode.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.PostAsync("/api/validatetoken?token=" + requestToken, content: null).DefaultTimeout();

        // Assert
        Assert.Equal(statusCode, response.StatusCode);

        if (result != null)
        {
            var actualResult = await response.Content.ReadFromJsonAsync<bool>();
            Assert.Equal(result, actualResult);
        }
    }

    [Fact]
    public async Task LogOutput_NoToken_GeneratedTokenLogged()
    {
        // Arrange
        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
        }, testSink: testSink);

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName && w.LogLevel >= LogLevel.Information).ToList();
        Assert.Collection(l,
            w =>
            {
                Assert.Equal("Aspire version: {Version}", LogTestHelpers.GetValue(w, "{OriginalFormat}"));
            },
            w =>
            {
                Assert.Equal("Now listening on: {DashboardUri}", LogTestHelpers.GetValue(w, "{OriginalFormat}"));

                var uri = new Uri((string)LogTestHelpers.GetValue(w, "DashboardUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/gRPC listening on: {OtlpEndpointUri}", LogTestHelpers.GetValue(w, "{OriginalFormat}"));

                var uri = new Uri((string)LogTestHelpers.GetValue(w, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/HTTP listening on: {OtlpEndpointUri}", LogTestHelpers.GetValue(w, "{OriginalFormat}"));

                var uri = new Uri((string)LogTestHelpers.GetValue(w, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP server is unsecured. Untrusted apps can send telemetry to the dashboard. For more information, visit https://go.microsoft.com/fwlink/?linkid=2267030", LogTestHelpers.GetValue(w, "{OriginalFormat}"));
                Assert.Equal(LogLevel.Warning, w.LogLevel);
            },
            w =>
            {
                Assert.Equal("Dashboard API is unsecured. Untrusted apps can access sensitive telemetry data.", LogTestHelpers.GetValue(w, "{OriginalFormat}"));
                Assert.Equal(LogLevel.Warning, w.LogLevel);
            },
            w =>
            {
                Assert.Equal("Login to the dashboard at {DashboardLoginUrl}", LogTestHelpers.GetValue(w, "{OriginalFormat}"));

                var uri = new Uri((string)LogTestHelpers.GetValue(w, "DashboardLoginUrl")!, UriKind.Absolute);
                var queryString = HttpUtility.ParseQueryString(uri.Query);
                Assert.NotNull(queryString["t"]);
            });
    }

    [Theory]
    [InlineData("http://+:0", "localhost")]
    [InlineData("http://0.0.0.0:0", "localhost")]
    [InlineData("http://127.0.0.1:0", "127.0.0.1")]
    [InlineData("http://aspire-test-hostname:0", "aspire-test-hostname")]
    public async Task LogOutput_AnyIP_LoginLinkLocalhost(string frontendUrl, string linkHost)
    {
        // Arrange
        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = frontendUrl;
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
        }, testSink: testSink);

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName).ToList();

        // Testing via the log template is kind of hacky. If this becomes a problem then consider adding proper log definitions and match via ID.
        var loginLinkLog = l.Single(w => "Login to the dashboard at {DashboardLoginUrl}" == (string?)LogTestHelpers.GetValue(w, "{OriginalFormat}"));

        var uri = new Uri((string)LogTestHelpers.GetValue(loginLinkLog, "DashboardLoginUrl")!, UriKind.Absolute);
        var queryString = HttpUtility.ParseQueryString(uri.Query);
        Assert.NotNull(queryString["t"]);

        Assert.Equal(linkHost, uri.Host);
    }

    [Fact]
    public async Task LogOutput_InContainer_LoginLinkContainerMessage()
    {
        // Arrange
        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config["DOTNET_RUNNING_IN_CONTAINER"] = "true";
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
        }, testSink: testSink);

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName).ToList();

        // Testing via the log template is kind of hacky. If this becomes a problem then consider adding proper log definitions and match via ID.
        Assert.Single(l, w => (string?)LogTestHelpers.GetValue(w, "{OriginalFormat}") == "Login to the dashboard at {DashboardLoginUrl} . The URL may need changes depending on how network access to the container is configured.");
    }
}
