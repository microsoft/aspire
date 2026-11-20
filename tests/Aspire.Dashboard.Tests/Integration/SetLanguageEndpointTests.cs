// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Localization;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class SetLanguageEndpointTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public SetLanguageEndpointTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task Get_SetLanguage_Http_CultureCookieIsNotSecure()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClient(new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}"));

        var response = await client.GetAsync(DashboardUrls.SetLanguageUrl("fr", "/hi")).DefaultTimeout();

        var cookie = AssertCultureCookie(response);
        // Browsers drop Secure cookies set over plain HTTP from non-localhost origins, which would break language selection.
        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_SetLanguage_Https_CultureCookieIsSecure()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://127.0.0.1:0;http://127.0.0.1:0";
        });
        await app.StartAsync().DefaultTimeout();

        var httpsEndpoint = app.FrontendEndPointsAccessor
            .Select(accessor => accessor())
            .Single(endpoint => endpoint.IsHttps);

        using var client = CreateClient(new Uri(httpsEndpoint.GetResolvedAddress()));

        var response = await client.GetAsync(DashboardUrls.SetLanguageUrl("fr", "/hi")).DefaultTimeout();

        var cookie = AssertCultureCookie(response);
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_SetLanguage_ForwardedHttps_CultureCookieIsSecure()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.ForwardedHeaders.ConfigKey] = bool.TrueString;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClient(new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}"));
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost");

        var response = await client.GetAsync(DashboardUrls.SetLanguageUrl("fr", "/hi")).DefaultTimeout();

        var cookie = AssertCultureCookie(response);
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_SetLanguage_ForwardedHttpsHeaderIgnoredWhenForwardedHeadersDisabled_CultureCookieIsNotSecure()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClient(new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}"));
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync(DashboardUrls.SetLanguageUrl("fr", "/hi")).DefaultTimeout();

        var cookie = AssertCultureCookie(response);
        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_SetLanguage_UnsupportedLanguage_NoCultureCookie()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var client = CreateClient(new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}"));

        var response = await client.GetAsync(DashboardUrls.SetLanguageUrl("xx", "/hi")).DefaultTimeout();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static HttpClient CreateClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false
        };

        return new HttpClient(handler) { BaseAddress = baseAddress };
    }

    private static string AssertCultureCookie(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/hi", response.Headers.Location?.OriginalString);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var expectedValue = Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("fr")));
        Assert.StartsWith($"{CookieRequestCultureProvider.DefaultCookieName}={expectedValue};", cookie, StringComparison.Ordinal);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; expires=", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie;
    }
}
