// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class FrontendPageAuthorizationTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public FrontendPageAuthorizationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/error")]
    public async Task Get_AnonymousPage_Unauthenticated_ReturnsPage(string path)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "TestKey123!";
        });
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClientWithoutRedirects(app);

        var response = await client.GetAsync(path).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces")]
    [InlineData("/metrics")]
    [InlineData("/consolelogs")]
    [InlineData("/terminals")]
    [InlineData("/extensions")]
    [InlineData("/error/404")]
    public async Task Get_ProtectedPage_Unauthenticated_RedirectToLogin(string path)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "TestKey123!";
        });
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClientWithoutRedirects(app);

        var response = await client.GetAsync(path).DefaultTimeout();

        var location = new Uri(client.BaseAddress!, response.Headers.Location!);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(DashboardUrls.LoginUrl(returnUrl: path), location.PathAndQuery, ignoreCase: true);
    }

    private static HttpClient CreateClientWithoutRedirects(DashboardWebApplication app)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}")
        };
    }
}
