// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Web;
using Aspire.Dashboard.Authentication;
using Aspire.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class FrontendOpenIdConnectAuthTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Login_AuthorizationCodeCallback_CompletesSignIn(bool resourceServiceEnabled, bool formPost)
    {
        await using var authority = await MockOpenIdAuthority.CreateAsync().DefaultTimeout();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            additionalConfiguration: config =>
            {
                ConfigureOpenIdConnect(config, authority);
                config[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://127.0.0.1:0";
                if (!resourceServiceEnabled)
                {
                    config.Remove(DashboardConfigNames.ResourceServiceUrlName.ConfigKey);
                }
            },
            preConfigureBuilder: builder =>
            {
                builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.IssuerSigningKey = authority.SigningKey;
                });
            });
        await app.StartAsync().DefaultTimeout();

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(app.FrontendSingleEndPointAccessor().GetResolvedAddress())
        };

        var challenge = await client.GetAsync("/structuredlogs").DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.NotNull(challenge.Headers.Location);
        var authorizationUrl = QueryHelpers.AddQueryString(challenge.Headers.Location.ToString(), "preservedExtraQueryParams", "true");
        var authorization = await client.GetAsync(authorizationUrl).DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        Assert.NotNull(authorization.Headers.Location);
        Assert.Equal("/", authorization.Headers.Location.AbsolutePath);

        // Follow the real code/state callback with the nonce and correlation cookies from
        // the challenge. A page redirect here would skip token exchange and never sign in.
        using var callbackRequest = new HttpRequestMessage(formPost ? HttpMethod.Post : HttpMethod.Get, authorization.Headers.Location);
        if (formPost)
        {
            // The test authority returns code/state in its redirect. Replay those values
            // as form fields to also exercise OIDC's default response_mode=form_post.
            var query = HttpUtility.ParseQueryString(authorization.Headers.Location.Query);
            var code = Assert.IsType<string>(query["code"]);
            var state = Assert.IsType<string>(query["state"]);
            callbackRequest.RequestUri = new Uri(authorization.Headers.Location.GetLeftPart(UriPartial.Path));
            callbackRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["state"] = state
            });
        }

        var callback = await client.SendAsync(callbackRequest).DefaultTimeout();
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/structuredlogs", callback.Headers.Location?.OriginalString);

        var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var authCookie = handler.CookieContainer.GetCookies(client.BaseAddress)[cookieOptions.Cookie.Name!];
        Assert.NotNull(authCookie);
        var ticket = cookieOptions.TicketDataFormat.Unprotect(authCookie.Value);
        Assert.NotNull(ticket);
        Assert.True(ticket.Principal.Identity?.IsAuthenticated);
        Assert.Equal("Jane Doe", ticket.Principal.FindFirst("name")?.Value);

        var authenticatedResponse = await client.GetAsync("/structuredlogs").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);

        if (!resourceServiceEnabled)
        {
            var homeResponse = await client.GetAsync("/").DefaultTimeout();
            Assert.Equal(HttpStatusCode.Redirect, homeResponse.StatusCode);
            Assert.Equal("/structuredlogs", homeResponse.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task Get_Unauthenticated_ForwardedHttps_UsesExternalCallbackUrl()
    {
        await using var authority = await MockOpenIdAuthority.CreateAsync().DefaultTimeout();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            ConfigureOpenIdConnect(config, authority);
            config.Remove(DashboardConfigNames.ResourceServiceUrlName.ConfigKey);
            config[DashboardConfigNames.ForwardedHeaders.ConfigKey] = bool.TrueString;
        });
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.FrontendSingleEndPointAccessor().GetResolvedAddress())
        };
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "dashboard.example.test");
        var response = await client.GetAsync("/structuredlogs").DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var query = HttpUtility.ParseQueryString(response.Headers.Location.Query);
        Assert.Equal("https://dashboard.example.test/", query["redirect_uri"]);
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.NotEmpty(cookies);
        Assert.All(cookies, cookie => Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Get_Unauthenticated_RedirectsToAuthority()
    {
        await using var authority = await MockOpenIdAuthority.CreateAsync().DefaultTimeout();

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            additionalConfiguration: config =>
            {
                ConfigureOpenIdConnect(config, authority);
            });

        await app.StartAsync().DefaultTimeout();

        var handler = new HttpClientHandler()
        {
            // Don't follow redirects. We want to validate where the redirect would take us.
            AllowAutoRedirect = false
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync("/").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var redirectedTo = response.Headers.Location;

        Assert.NotNull(redirectedTo);
        Assert.True(redirectedTo.IsAbsoluteUri);
        Assert.Equal("localhost", redirectedTo.Host);
        Assert.Equal("/authorize", redirectedTo.AbsolutePath);

        var query = HttpUtility.ParseQueryString(redirectedTo.Query);
        Assert.Equal("MyClientId", query.Get("client_id"));
        Assert.Equal("code", query.Get("response_type"));
        Assert.Equal("openid profile", query.Get("scope"));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task Get_Unauthenticated_OtlpHttpConnection_Denied()
    {
        await using var authority = await MockOpenIdAuthority.CreateAsync().DefaultTimeout();

        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            additionalConfiguration: config =>
            {
                ConfigureOpenIdConnect(config, authority);
            },
            testSink: testSink);

        await app.StartAsync().DefaultTimeout();

        var handler = new HttpClientHandler()
        {
            // Don't follow redirects. We want to validate where the redirect would take us.
            AllowAutoRedirect = false
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync("/").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var log = testSink.Writes.Single(s => s.LoggerName == typeof(FrontendCompositeAuthenticationHandler).FullName && s.EventId.Name == "AuthenticationSchemeNotAuthenticatedWithFailure");
        Assert.Equal("FrontendComposite was not authenticated. Failure message: Connection types 'Frontend' are not enabled on this connection.", log.Message);

        await app.StopAsync().DefaultTimeout();
    }

    private static void ConfigureOpenIdConnect(Dictionary<string, string?> config, MockOpenIdAuthority.Authority authority)
    {
        // These tests request the resources page, so configure a resource service to keep
        // the standalone-page redirect from handling the initial request.
        config[DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey] = "Unsecured";
        config[DashboardConfigNames.ResourceServiceUrlName.ConfigKey] = "https://localhost:1234"; // won't actually exist

        // Configure OIDC. It (the RP) will communicate with the mock authority (IdP) spun up for this test.
        config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = "OpenIdConnect";
        config["Authentication:Schemes:OpenIdConnect:Authority"] = authority.Url;
        config["Authentication:Schemes:OpenIdConnect:ClientId"] = "MyClientId";
        config["Authentication:Schemes:OpenIdConnect:ClientSecret"] = "MyClientSecret";
        // Allow the requirement of HTTPS communication with the OpenIdConnect authority to be relaxed during tests.
        config["Authentication:Schemes:OpenIdConnect:RequireHttpsMetadata"] = "false";
    }
}
