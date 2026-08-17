// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Terminal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

// Endpoint-level coverage for the CSWSH defense. Unit tests on IsAllowedOrigin
// prove the helper works, but only an endpoint test proves the /api/terminal
// route is actually wired through that gate before reaching the resolver. A
// future refactor that moved origin checking elsewhere — or accidentally
// removed it from HandleAsync — would silently reopen CSWSH while leaving the
// IsAllowedOrigin unit tests green. These tests fail in exactly that
// scenario by verifying both the rejection HTTP status AND that the resolver
// is never invoked when the Origin gate rejects the upgrade.
public class TerminalWebSocketProxyEndpointTests
{
    private const string DashboardScheme = "https";
    private const string DashboardHost = "dashboard.example.com";

    [Fact]
    public async Task TerminalEndpoint_MissingOrigin_Returns403_AndDoesNotCallResolver()
    {
        var resolver = new TrackingTerminalConnectionResolver();
        using var host = await BuildHostAsync(resolver);
        var client = host.GetTestServer().CreateWebSocketClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            // No SetRequestHeader("Origin", ...) — TestHost will not synthesise
            // one, so the proxy sees a missing Origin header.
            await client.ConnectAsync(BuildTerminalUri(), CancellationToken.None);
        });

        Assert.Contains("403", ex.Message);
        Assert.False(resolver.ResolveCalled, "Resolver must not be invoked when the Origin gate rejects the upgrade.");
    }

    [Fact]
    public async Task TerminalEndpoint_DisallowedOrigin_Returns403_AndDoesNotCallResolver()
    {
        var resolver = new TrackingTerminalConnectionResolver();
        using var host = await BuildHostAsync(resolver);
        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["Origin"] = "https://evil.example.com";
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await client.ConnectAsync(BuildTerminalUri(), CancellationToken.None);
        });

        Assert.Contains("403", ex.Message);
        Assert.False(resolver.ResolveCalled, "Resolver must not be invoked when the Origin gate rejects the upgrade.");
    }

    [Fact]
    public async Task TerminalEndpoint_SameOrigin_ProceedsToResolver()
    {
        // Resolver returns null so the endpoint reports the resource as unavailable.
        // We don't care about the response code here — only that the resolver was
        // reached, which proves the origin gate passed and execution flowed into
        // resource resolution.
        var resolver = new TrackingTerminalConnectionResolver();
        using var host = await BuildHostAsync(resolver);
        var client = host.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["Origin"] = $"{DashboardScheme}://{DashboardHost}";
        };

        // Allowed-origin path will still fail to upgrade because the fake
        // resolver returns null (resource not found) — the proxy responds 404,
        // which TestHost's WebSocketClient surfaces as InvalidOperationException
        // from ConnectAsync. The important assertion is that the resolver was
        // reached at all.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await client.ConnectAsync(BuildTerminalUri(), CancellationToken.None);
        });

        Assert.True(resolver.ResolveCalled, "Same-origin requests must proceed past the Origin gate to resource resolution.");
    }

    private static Uri BuildTerminalUri()
    {
        // TestHost rewrites Scheme/Host on dispatch; only the path+query matter.
        return new Uri($"{DashboardScheme}://{DashboardHost}/api/terminal?resource=myapp&replica=0");
    }

    private static async Task<IHost> BuildHostAsync(ITerminalConnectionResolver resolver)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(resolver);

                        // Permissive auth/authorization stack — these tests
                        // target the Origin gate, not RequireAuthorization.
                        services.AddAuthentication(AlwaysAuthenticatedHandler.SchemeName)
                                .AddScheme<AuthenticationSchemeOptions, AlwaysAuthenticatedHandler>(
                                    AlwaysAuthenticatedHandler.SchemeName, _ => { });
                        services.AddAuthorizationBuilder()
                                .AddPolicy(FrontendAuthorizationDefaults.PolicyName,
                                           policy => policy.RequireAuthenticatedUser());

                        services.AddLogging();
                    })
                    .Configure(app =>
                    {
                        // Force Scheme/Host to match what BuildTerminalUri sends so
                        // IsAllowedOrigin's same-origin comparison matches the test
                        // origin. TestServer's default Scheme is "http" and Host is
                        // "localhost"; we rewrite to the dashboard's public origin.
                        app.Use(async (ctx, next) =>
                        {
                            ctx.Request.Scheme = DashboardScheme;
                            ctx.Request.Host = new HostString(DashboardHost);
                            await next();
                        });

                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseWebSockets();

                        // Endpoints must be mapped against a WebApplication, but
                        // since this host uses Generic Host + UseTestServer we
                        // map the production handler manually with the same
                        // wiring as MapTerminalWebSocket. The point of the
                        // test is to lock down the origin-first ordering inside
                        // HandleAsync regardless of which Map* overload is used.
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.Map("/api/terminal", async (HttpContext context,
                                                                  ITerminalConnectionResolver r,
                                                                  ILoggerFactory loggerFactory) =>
                            {
                                var logger = loggerFactory.CreateLogger("Aspire.Dashboard.Terminal.TerminalWebSocketProxy");
                                await TerminalWebSocketProxy.HandleAsync(context, r, logger, "test");
                            }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);
                        });
                    });
            })
            .StartAsync();
    }

    private sealed class TrackingTerminalConnectionResolver : ITerminalConnectionResolver
    {
        public bool ResolveCalled { get; private set; }

        public Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
        {
            ResolveCalled = true;
            return Task.FromResult<Stream?>(null);
        }
    }

    private sealed class AlwaysAuthenticatedHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "AlwaysAuthenticated";

        public AlwaysAuthenticatedHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
                                          ILoggerFactory logger,
                                          UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(SchemeName);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user"));
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
