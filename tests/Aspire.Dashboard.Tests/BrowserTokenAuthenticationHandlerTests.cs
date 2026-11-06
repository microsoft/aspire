// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Claims;
using Aspire.Dashboard.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class BrowserTokenAuthenticationHandlerTests
{
    private const string BrowserTokenScheme = "TestBrowserToken";
    private const string SignInScheme = "TestCookie";
    private const string AuthenticatedClaim = "test-browser-token";

    [Theory]
    [InlineData("")]
    [InlineData("/dashboard")]
    public async Task Login_WithoutToken_ContinuesToLoginPage(string pathBase)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"{pathBase}/login").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(bool.FalseString, await response.Content.ReadAsStringAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("", "/authenticate")]
    [InlineData("/dashboard", "/")]
    [InlineData("/dashboard", "/authenticate")]
    public async Task Login_ValidToken_CookieContainsBrowserTokenIdentity(string pathBase, string path)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase).DefaultTimeout();
        using var client = host.GetTestClient();
        var loginResponse = await client.GetAsync($"{pathBase}/login?t=token").DefaultTimeout();

        var cookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie"));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.IsNullOrEmpty(pathBase) ? "/" : pathBase, SetCookieHeaderValue.Parse(cookie).Path.Value);
        using var request = new HttpRequestMessage(HttpMethod.Get, pathBase + path);
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var response = await client.SendAsync(request).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bool.TrueString, await response.Content.ReadAsStringAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData("", false, "http://localhost/login?returnUrl=%2Fchallenge%3Ffilter%3Dtest")]
    [InlineData("", true, "http://localhost/login?returnUrl=%2Fchallenge%3Ffilter%3Dtest")]
    [InlineData("/dashboard", false, "http://localhost/dashboard/login?returnUrl=%2Fdashboard%2Fchallenge%3Ffilter%3Dtest")]
    [InlineData("/dashboard", true, "http://localhost/dashboard/login?returnUrl=%2Fdashboard%2Fchallenge%3Ffilter%3Dtest")]
    public async Task Challenge_RedirectsToLogin_WithReturnUrl(string pathBase, bool ajax, string expectedLocation)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase).DefaultTimeout();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{pathBase}/challenge?filter=test");
        if (ajax)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

        var response = await host.GetTestClient().SendAsync(request).DefaultTimeout();

        Assert.Equal(ajax ? HttpStatusCode.Unauthorized : HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("", null, "http://localhost/Account/AccessDenied?ReturnUrl=%2Fforbid")]
    [InlineData("/dashboard", null, "http://localhost/dashboard/Account/AccessDenied?ReturnUrl=%2Fdashboard%2Fforbid")]
    [InlineData("", "/test", "http://localhost/Account/AccessDenied?ReturnUrl=%2Ftest")]
    [InlineData("/dashboard", "/dashboard/test", "http://localhost/dashboard/Account/AccessDenied?ReturnUrl=%2Fdashboard%2Ftest")]
    public async Task Forbid_DelegatesToCookieScheme_WithProperties(string pathBase, string? returnUrl, string expectedLocation)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase).DefaultTimeout();
        var path = $"{pathBase}/forbid";
        if (returnUrl is not null)
        {
            path += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        var response = await host.GetTestClient().GetAsync(path).DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(true, "/login?t=token", "/dashboard/", true)]
    [InlineData(true, "/login?t=token&returnUrl=%2Fdashboard%2Ftest", "/dashboard/test", true)]
    [InlineData(true, "/login?t=token&returnUrl=https%3A%2F%2Fevil.com", "/dashboard/", true)]
    [InlineData(true, "/login?t=wrong", "/dashboard/login", false)]
    [InlineData(true, "/login?t=wrong&returnUrl=%2Fdashboard%2Ftest", "/dashboard/login?returnUrl=%2fdashboard%2ftest", false)]
    [InlineData(false, "/login?t=test", "/dashboard/", false)]
    [InlineData(false, "/login?t=test&returnUrl=%2Fdashboard%2Ftest", "/dashboard/test", false)]
    [InlineData(false, "/login?t=test&returnUrl=https%3A%2F%2Fevil.com", "/dashboard/", false)]
    public async Task Login_WithPathBase_PreservesRedirects(bool enabled, string path, string expectedLocation, bool setsCookie)
    {
        using var host = await SetUpHostAsync(enabled, "token", "/dashboard").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/dashboard" + path).DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
        Assert.Equal(setsCookie, response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ValidateToken_NotBrowserTokenAuth_RedirectedToHomepage()
    {
        using var host = await SetUpHostAsync(enabled: false, string.Empty, pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=test").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_NotBrowserTokenAuth_RedirectedToReturnUrl()
    {
        using var host = await SetUpHostAsync(enabled: false, string.Empty, pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=test&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/test", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_RedirectsToLogin()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=wrong").DefaultTimeout();
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_WrongToken_RedirectsToLogin_WithReturnUrl()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=wrong&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/login?returnUrl=%2ftest", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_RedirectsToHome()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=token").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_RedirectsToReturnUrl()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/login?t=token&returnUrl=/test").DefaultTimeout();
        Assert.Equal("/test", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    public async Task ValidateToken_NotBrowserTokenAuth_AbsoluteReturnUrl_RedirectsToHomepage(string returnUrl)
    {
        using var host = await SetUpHostAsync(enabled: false, string.Empty, pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"/login?t=test&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    public async Task ValidateToken_BrowserTokenAuth_RightToken_AbsoluteReturnUrl_RedirectsToHomepage(string returnUrl)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", pathBase: "").DefaultTimeout();
        var response = await host.GetTestClient().GetAsync($"/login?t=token&returnUrl={Uri.EscapeDataString(returnUrl)}").DefaultTimeout();
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/signin?opaque=token", "/tenant/landing", true)]
    [InlineData("/signin?opaque=token&next=%2Ftenant%2Fhome", "/tenant/home", true)]
    [InlineData("/signin?opaque=token&next=https%3A%2F%2Fevil.com", "/tenant/landing", true)]
    [InlineData("/signin?opaque=wrong&next=%2Ftenant%2Fhome", "/tenant/signin?next=%2ftenant%2fhome", false)]
    public async Task Login_CustomOptions_UsesConfiguredPathsAndParameters(string path, string expectedLocation, bool setsCookie)
    {
        using var host = await SetUpHostAsync(enabled: true, "token", "/tenant", options =>
        {
            options.LoginPath = "/signin";
            options.DefaultRedirectPath = "/landing";
            options.TokenParameter = "opaque";
            options.ReturnUrlParameter = "next";
        }).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/tenant" + path).DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
        Assert.Equal(setsCookie, response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Challenge_CustomOptions_UsesConfiguredLoginPathAndReturnUrlParameter()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", "/tenant", options =>
        {
            options.LoginPath = "/signin";
            options.ReturnUrlParameter = "next";
        }).DefaultTimeout();
        var response = await host.GetTestClient().GetAsync("/tenant/challenge?filter=test").DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://localhost/tenant/signin?next=%2Ftenant%2Fchallenge%3Ffilter%3Dtest", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ValidateToken_CustomOptions_UsesConfiguredEndpointAndCookiePersistence()
    {
        using var host = await SetUpHostAsync(enabled: true, "token", "/tenant", options =>
        {
            options.TokenValidationPath = "/signin/validate";
            options.IsPersistent = false;
        }).DefaultTimeout();
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync("/tenant/signin/validate", new { Token = "token" }).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await response.Content.ReadFromJsonAsync<bool>().DefaultTimeout());
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues("Set-Cookie")));
        Assert.Equal("/tenant", cookie.Path.Value);
        Assert.Null(cookie.Expires);

        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "/tenant/authenticate");
        authenticatedRequest.Headers.Add("Cookie", $"{cookie.Name}={cookie.Value}");
        var authenticatedResponse = await client.SendAsync(authenticatedRequest).DefaultTimeout();
        Assert.Equal(bool.TrueString, await authenticatedResponse.Content.ReadAsStringAsync().DefaultTimeout());
    }

    [Fact]
    public void Options_EnabledWithoutToken_Throws()
    {
        var options = new BrowserTokenAuthenticationOptions { SignInScheme = SignInScheme };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(BrowserTokenScheme));
        Assert.Equal("Token must be configured when browser token authentication is enabled.", exception.Message);
    }

    [Fact]
    public void Options_SignInSchemeMatchesHandlerScheme_Throws()
    {
        var options = new BrowserTokenAuthenticationOptions { Token = "token", SignInScheme = BrowserTokenScheme };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(BrowserTokenScheme));
        Assert.Equal("SignInScheme must not be the browser token authentication scheme.", exception.Message);
    }

    [Theory]
    [InlineData(null, "DefaultCookie", null, "DefaultCookie")]
    [InlineData(null, "DefaultCookie", "SignInCookie", "SignInCookie")]
    [InlineData(null, null, "SignInCookie", "SignInCookie")]
    [InlineData("ExplicitCookie", "DefaultCookie", "SignInCookie", "ExplicitCookie")]
    [InlineData("ExplicitCookie", null, null, "ExplicitCookie")]
    public void Registration_ResolvesSignInScheme(string? explicitScheme, string? defaultScheme, string? defaultSignInScheme, string expectedScheme)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddBrowserToken(BrowserTokenScheme, options =>
            {
                options.Token = "token";
                options.SignInScheme = explicitScheme;
            });
        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = defaultScheme;
            options.DefaultSignInScheme = defaultSignInScheme;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<BrowserTokenAuthenticationOptions>>().Get(BrowserTokenScheme);

        Assert.Equal(expectedScheme, options.SignInScheme);
    }

    [Fact]
    public void Registration_NoSignInSchemeOrDefault_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SignInScheme;
            options.DefaultChallengeScheme = BrowserTokenScheme;
        }).AddBrowserToken(BrowserTokenScheme, options => options.Token = "token");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<BrowserTokenAuthenticationOptions>>().Get(BrowserTokenScheme));

        Assert.Equal("SignInScheme must be configured, or the application must configure a default sign-in scheme or default scheme.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registration_DefaultSignInSchemeMatchesHandler_Throws(bool useDefaultSignInScheme)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(options =>
        {
            if (useDefaultSignInScheme)
            {
                options.DefaultSignInScheme = BrowserTokenScheme;
            }
            else
            {
                options.DefaultScheme = BrowserTokenScheme;
            }
        }).AddBrowserToken(BrowserTokenScheme, options => options.Token = "token");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<BrowserTokenAuthenticationOptions>>().Get(BrowserTokenScheme));

        Assert.Equal("SignInScheme must not be the browser token authentication scheme.", exception.Message);
    }

    [Fact]
    public void Registration_MultipleSchemes_PreservesNamedOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(SignInScheme)
            .AddBrowserToken(BrowserTokenScheme, options => options.Token = "first-token")
            .AddBrowserToken("SecondBrowserToken", "Second login", options =>
            {
                options.Token = "second-token";
                options.SignInScheme = "SecondCookie";
            });
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<BrowserTokenAuthenticationOptions>>();

        Assert.Equal(SignInScheme, monitor.Get(BrowserTokenScheme).SignInScheme);
        Assert.Equal("first-token", monitor.Get(BrowserTokenScheme).Token);
        Assert.Equal("SecondCookie", monitor.Get("SecondBrowserToken").SignInScheme);
        Assert.Equal("second-token", monitor.Get("SecondBrowserToken").Token);
        var schemes = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.Schemes;
        Assert.Equal("Second login", Assert.Single(schemes, scheme => scheme.Name == "SecondBrowserToken").DisplayName);
    }

    private static async Task<IHost> SetUpHostAsync(bool enabled, string expectedToken, string pathBase, Action<BrowserTokenAuthenticationOptions>? configureOptions = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddAuthentication(o =>
                        {
                            o.DefaultScheme = SignInScheme;
                            o.DefaultChallengeScheme = BrowserTokenScheme;
                        })
                            .AddCookie(SignInScheme)
                            .AddBrowserToken(BrowserTokenScheme, o =>
                            {
                                o.Enabled = enabled;
                                o.Token = expectedToken;
                                o.TokenValidationPath = "/api/validatetoken";
                                o.Claims.Add(new Claim(ClaimTypes.NameIdentifier, "Test user"));
                                o.Claims.Add(new Claim(AuthenticatedClaim, bool.TrueString));
                                configureOptions?.Invoke(o);
                            });
                    })
                    .Configure(app =>
                    {
                        app.UsePathBase(pathBase);
                        app.UseAuthentication();
                        app.Run(async context =>
                        {
                            if (context.Request.Path == "/challenge")
                            {
                                await context.ChallengeAsync();
                                return;
                            }

                            if (context.Request.Path == "/authenticate")
                            {
                                var result = await context.AuthenticateAsync(BrowserTokenScheme);
                                Assert.True(result.Succeeded);
                                context.User = result.Principal;
                            }

                            if (context.Request.Path == "/forbid")
                            {
                                await context.ForbidAsync(
                                    BrowserTokenScheme,
                                    new AuthenticationProperties { RedirectUri = context.Request.Query["returnUrl"].ToString() });
                                return;
                            }

                            var authenticated = context.User.Identity?.IsAuthenticated == true &&
                                context.User.HasClaim(ClaimTypes.NameIdentifier, "Test user") &&
                                context.User.HasClaim(AuthenticatedClaim, bool.TrueString);
                            await context.Response.WriteAsync(authenticated.ToString());
                        });
                    });
            })
            .StartAsync();
    }
}
