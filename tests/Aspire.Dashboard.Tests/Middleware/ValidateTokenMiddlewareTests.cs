// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Dashboard.Tests.Middleware;

public class ValidateTokenMiddlewareTests
{
    [Fact]
    public async Task ValidateToken_NotBrowserTokenAuth_RedirectedToHomepage()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.Unsecured, string.Empty).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=test").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_NotBrowserTokenAuth_RedirectedToReturnUrl()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.Unsecured, string.Empty).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=test&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/test", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_RedirectsToLogin()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=wrong").DefaultTimeout();
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_RedirectsToLogin_WithReturnUrl()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=wrong&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/login?returnUrl=%2ftest", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_RedirectsToHome()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=token").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_RedirectsToReturnUrl()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=token&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/test", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    [InlineData("///evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("\\/evil.com")]
    [InlineData("evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("~//evil.com")]
    [InlineData("~/\\evil.com")]
    [InlineData("/\t/evil.com")]
    [InlineData("/\n/evil.com")]
    [InlineData("/\r/evil.com")]
    public async Task ValidateToken_NotBrowserTokenAuth_AbsoluteReturnUrl_RedirectsToHomepage(string returnUrl)
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.Unsecured, string.Empty).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"/login?t=test&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    [InlineData("///evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("\\/evil.com")]
    [InlineData("evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("~//evil.com")]
    [InlineData("~/\\evil.com")]
    [InlineData("/\t/evil.com")]
    [InlineData("/\n/evil.com")]
    [InlineData("/\r/evil.com")]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_AbsoluteReturnUrl_RedirectsToHomepage(string returnUrl)
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"/login?t=token&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    // The return URL is passed as-is (already percent-encoded) to cover encoded variants of non-local URLs.
    // ASP.NET Core decodes the query value once, so a double-encoded value stays a non-local relative path.
    [Theory]
    [InlineData("%2F%2Fevil.com")]
    [InlineData("%2f%2fevil.com")]
    [InlineData("%2F%5Cevil.com")]
    [InlineData("%5C%5Cevil.com")]
    [InlineData("%5C%2Fevil.com")]
    [InlineData("%252F%252Fevil.com")]
    [InlineData("%252F%255Cevil.com")]
    [InlineData("https%3A%2F%2Fevil.com")]
    [InlineData("https%3A%2f%2fevil.com")]
    [InlineData("%68ttps://evil.com")]
    [InlineData("javascript%3Aalert(1)")]
    [InlineData("%2F%09%2Fevil.com")]
    [InlineData("%2F%0A%2Fevil.com")]
    [InlineData("%2F%0D%2Fevil.com")]
    [InlineData("%7E%2F%2Fevil.com")]
    public async Task ValidateToken_EncodedNonLocalReturnUrl_RedirectsToHomepage(string encodedReturnUrl)
    {
        using var unsecuredHost = await SetUpHostAsync(FrontendAuthMode.Unsecured, string.Empty).DefaultTimeout();
        var unsecuredResponse = await unsecuredHost.GetTestClient().GetAsync($"/login?t=test&returnUrl={encodedReturnUrl}").DefaultTimeout();
        Assert.Equal("/", unsecuredResponse.Headers.Location?.OriginalString);

        using var browserTokenHost = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var browserTokenResponse = await browserTokenHost.GetTestClient().GetAsync($"/login?t=token&returnUrl={encodedReturnUrl}").DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, browserTokenResponse.StatusCode);
        Assert.Equal("/", browserTokenResponse.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/test", "/test")]
    [InlineData("/test?a=1&b=2", "/test?a=1&b=2")]
    [InlineData("/test#fragment", "/test#fragment")]
    [InlineData("/", "/")]
    [InlineData("/%2Fevil.com", "/%2Fevil.com")] // A percent-encoded slash is part of the path, not a protocol-relative URL.
    public async Task ValidateToken_LocalReturnUrl_RedirectsToReturnUrl(string returnUrl, string expectedLocation)
    {
        using var unsecuredHost = await SetUpHostAsync(FrontendAuthMode.Unsecured, string.Empty).DefaultTimeout();
        var unsecuredResponse = await unsecuredHost.GetTestClient().GetAsync($"/login?t=test&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal(expectedLocation, unsecuredResponse.Headers.Location?.OriginalString);

        using var browserTokenHost = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var browserTokenResponse = await browserTokenHost.GetTestClient().GetAsync($"/login?t=token&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, browserTokenResponse.StatusCode);
        Assert.Equal(expectedLocation, browserTokenResponse.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("https://evil.com")]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_NonLocalReturnUrl_RedirectsToLoginOnSameHost(string returnUrl)
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"/login?t=wrong&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();

        // The failure redirect only removes the token from the query string. The return URL is carried along as an escaped
        // query value and is validated again after the user signs in.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        Assert.StartsWith("/login?returnUrl=", location, StringComparison.Ordinal);
        // Don't use Uri.TryCreate(..., UriKind.Absolute) to detect an external redirect. On Linux and macOS a rooted path such as
        // "/login" is parsed as an absolute file: URI, so that check passes or fails depending on the OS.
        Assert.True(RedirectHttpResult.IsLocalUrl(location));
        Assert.DoesNotContain("t=wrong", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_LoginPathCasePreserved()
    {
        using var host = await SetUpHostAsync(FrontendAuthMode.BrowserToken, "token").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/LOGIN?t=wrong&other=value").DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/LOGIN?other=value", response.Headers.Location?.OriginalString);
    }

    private static async Task<IHost> SetUpHostAsync(FrontendAuthMode authMode, string expectedToken)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddAuthentication().AddCookie();

                        services.Configure<DashboardOptions>(o =>
                        {
                            o.Frontend = new FrontendOptions
                            {
                                AuthMode = authMode,
                                BrowserToken = expectedToken,
                                EndpointUrls = "http://localhost/" // required for TryParseOptions
                            };

                            Assert.True(o.Frontend.TryParseOptions(out _));
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<ValidateTokenMiddleware>();
                    });
            })
            .StartAsync();
    }
}
