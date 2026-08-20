// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

internal static class MockOpenIdAuthority
{
    /// <summary>
    /// Creates a mock authority (identity provider) that will handle the basics of the protocol, for use in automated tests.
    /// </summary>
    public static async Task<Authority> CreateAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.ConfigureServices(services => services.AddRouting())
                    .UseKestrel(options =>
                    {
                        // Bind to loopback on a random available port
                        options.Listen(IPAddress.Loopback, 0);
                    })
                    .Configure(app =>
                    {
                        // Based on code from https://github.com/dotnet/aspnetcore/blob/3f99d45b0b7d8f0427a3d98acc63098694613362/src/Components/test/testassets/Components.TestServer/RemoteAuthenticationStartup.cs#L37-L94

                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            var issuer = "";
                            var lastCode = "";
                            var jwtHandler = new JsonWebTokenHandler();

                            endpoints.MapGet(
                                ".well-known/openid-configuration",
                                (HttpRequest request, [FromHeader] string host) =>
                                {
                                    issuer = $"{(request.IsHttps ? "https" : "http")}://{host}";
                                    return Results.Json(new
                                    {
                                        issuer,
                                        authorization_endpoint = $"{issuer}/authorize",
                                        token_endpoint = $"{issuer}/token",
                                    });
                                });

                            endpoints.MapGet(
                                "authorize",
                                (string redirect_uri, string? state, string? prompt, bool? preservedExtraQueryParams) =>
                                {
                                    // Require interaction so silent sign-in does not skip RedirectToLogin.razor.
                                    if (prompt == "none")
                                    {
                                        return Results.Redirect($"{redirect_uri}?error=interaction_required&state={state}");
                                    }

                                    // Verify that the extra query parameters added by RedirectToLogin.razor are preserved.
                                    if (preservedExtraQueryParams != true)
                                    {
                                        return Results.Redirect($"{redirect_uri}?error=invalid_request&error_description=extraQueryParams%20not%20preserved&state={state}");
                                    }

                                    lastCode = Random.Shared.Next().ToString(CultureInfo.InvariantCulture);
                                    return Results.Redirect($"{redirect_uri}?code={lastCode}&state={state}");
                                });

                            endpoints.MapPost(
                                "token",
                                ([FromForm] string code) =>
                                {
                                    if (string.IsNullOrEmpty(lastCode) && code != lastCode)
                                    {
                                        return Results.BadRequest("Bad code");
                                    }

                                    return Results.Json(new
                                    {
                                        token_type = "Bearer",
                                        scope = "openid profile",
                                        expires_in = 3600,
                                        id_token = jwtHandler.CreateToken(new SecurityTokenDescriptor
                                        {
                                            Issuer = issuer,
                                            Audience = "s6BhdRkqt3",
                                            Claims = new Dictionary<string, object>
                                            {
                                                ["sub"] = "248289761001",
                                                ["name"] = "Jane Doe",
                                            },
                                        }),
                                    });
                                }).DisableAntiforgery();
                        });
                    });
            })
            .ConfigureLogging(logging =>
            {
                // Log to the console
                logging.AddConsole();
            })
            .Build();

        await host.StartAsync();

        return new Authority(host, url: GetBoundAddress());

        string GetBoundAddress()
        {
            var serverAddress = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();

            Assert.NotNull(serverAddress);

            var authorityUrl = serverAddress.Addresses.First().Replace("127.0.0.1", "localhost");

            Assert.StartsWith("http://localhost", authorityUrl);

            return authorityUrl;
        }
    }

    public sealed class Authority(IHost host, string url) : IAsyncDisposable
    {
        public string Url => url;

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
