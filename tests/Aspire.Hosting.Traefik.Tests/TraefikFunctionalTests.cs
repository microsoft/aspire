// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikFunctionalTests(ITestOutputHelper testOutputHelper)
{
    // A tiny server that echoes the request, so the test can see which backend answered and with which headers.
    private const string WhoamiImage = "docker.io/traefik/whoami:v1.11";

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task RoutesRequestsByHostToContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        await using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        await WaitForResourcesAsync(app, traefik.Resource.Name, whoami.Resource.Name);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        // The route matches on the Host header, so send it explicitly rather than relying on *.localhost
        // resolving to loopback, which not every resolver does.
        using var routed = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token);
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        var body = await routed.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Host: whoami.dev.localhost", body);

        using var unrouted = new HttpRequestMessage(HttpMethod.Get, "/");
        unrouted.Headers.Host = "other.dev.localhost";
        using var notFound = await httpClient.SendAsync(unrouted, cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task AppliesMiddlewaresToRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var headers = traefik.AddMiddleware("tag", "headers")
            .WithOption("customRequestHeaders.X-Aspire-Middleware", "applied");
        var strip = traefik.AddMiddleware("strip", "stripPrefix")
            .WithOption("prefixes", ["/v1"]);

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"whoami.dev.localhost");
                route.PathPrefix = "/v1";
                route.Middlewares.Add(strip.Resource);
            })
            .WithTraefikMiddleware(headers);

        await using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        await WaitForResourcesAsync(app, traefik.Resource.Name, whoami.Resource.Name);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        using var response = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token, "/v1/test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // whoami prints the request line and headers it received: the prefix is gone and the header was added.
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("GET /test HTTP/1.1", body);
        Assert.Contains("X-Aspire-Middleware: applied", body);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task BasicAuthWithPlainPasswordAndStrippedPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var password = builder.AddParameter("password", "s3cret", secret: true);
        var traefik = builder.AddTraefik("traefik");
        var auth = traefik.AddBasicAuth("auth", "admin", password);

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"whoami.dev.localhost");
                route.PathPrefix = "/app";
                route.StripPathPrefix = true;
            })
            .WithTraefikMiddleware(auth);

        await using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        await WaitForResourcesAsync(app, traefik.Resource.Name, whoami.Resource.Name);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        using var anonymous = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token, "/app/test");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/app/test");
        request.Headers.Host = "whoami.dev.localhost";
        request.Headers.Authorization = new("Basic", Convert.ToBase64String("admin:s3cret"u8.ToArray()));
        using var authenticated = await httpClient.SendAsync(request, cts.Token);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        var body = await authenticated.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("GET /test HTTP/1.1", body);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ForwardAuthLetsAuthorizedRequestsThrough()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        // A stub authentication server: 200 with the user in X-User when X-Token is right, 401 otherwise.
        var authServer = builder.AddContainer("auth-server", "docker.io/library/nginx", "1.27-alpine")
            .WithHttpEndpoint(targetPort: 80)
            .WithContainerFiles("/etc/nginx/conf.d", [new ContainerFile
            {
                Name = "default.conf",
                Contents = """
                    server {
                        listen 80;
                        location /verify {
                            if ($http_x_token = "secret") {
                                add_header X-User alice;
                                return 200;
                            }
                            return 401;
                        }
                    }
                    """,
            }]);

        var check = traefik.AddForwardAuth("check", authServer.GetEndpoint("http"), "/verify",
            options => options.AuthResponseHeaders.Add("X-User"));

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost")
            .WithTraefikMiddleware(check);

        await using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        await WaitForResourcesAsync(app, traefik.Resource.Name, whoami.Resource.Name, authServer.Resource.Name);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        using var anonymous = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "whoami.dev.localhost";
        request.Headers.Add("X-Token", "secret");
        using var authorized = await httpClient.SendAsync(request, cts.Token);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var body = await authorized.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("X-User: alice", body);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);
    }

    private static async Task WaitForResourcesAsync(DistributedApplication app, string traefik, params string[] backends)
    {
        // Traefik has a health check; the backends don't, so Running is the best signal for them (see
        // SendWithRetryAsync). Waiting in parallel keeps the total wait the longest single one.
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);

        await Task.WhenAll(
            [
                app.ResourceNotifications.WaitForResourceHealthyAsync(traefik, cts.Token),
                .. backends.Select(backend => app.ResourceNotifications.WaitForResourceAsync(backend, KnownResourceStates.Running, cts.Token)),
            ]);
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient httpClient, string host, CancellationToken cancellationToken, string path = "/")
    {
        // Traefik reports healthy before the file provider has necessarily loaded the dynamic configuration, so the
        // first requests can still get 404. A backend container is Running before its server listens, and until then
        // Traefik answers 502 (or 500 from a forward-auth server it can't reach), so those are retried too. 503 is
        // what Traefik returns while a route has no healthy server.
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}
