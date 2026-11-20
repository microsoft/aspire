// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamplesIntegrationTests.Infrastructure;
using Xunit;
using static Aspire.Playground.Tests.TraefikPlaygroundFixture;

namespace Aspire.Playground.Tests;

/// <summary>
/// Checks the features <c>playground/traefik</c>'s README lists, through Traefik's entry points. The app is started
/// once for all tests by <see cref="TraefikPlaygroundFixture"/>.
/// </summary>
[RequiresFeature(TestFeature.ContainerRuntime)]
[OuterloopTest("Starts the whole Traefik playground, including Keycloak, and signs in through it")]
public partial class TraefikPlaygroundTests(TraefikPlaygroundFixture playground) : IClassFixture<TraefikPlaygroundFixture>
{
    // From playground/traefik/Traefik.AppHost/Realms/traefik-demo-realm.json.
    private const string SsoUser = "alice";
    private const string SsoEmail = "alice@example.com";

    private string Http(string host, string path = "/") => $"http://{host}.{Domain}:{playground.WebPort}{path}";

    private string Https(string host, string path = "/") => $"https://{host}.{Domain}:{playground.WebSecurePort}{path}";

    private AuthenticationHeaderValue AdminCredentials =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{playground.AdminPassword}")));

    [Fact]
    public async Task OpenRouteReachesContainerWithMiddlewareHeaders()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(Http("whoami"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"Host: whoami.{Domain}:{playground.WebPort}", body);
        Assert.Contains("X-Forwarded-Proto: http", body);
        // The `served-by` entry-point middleware and the `defaults` chain's security headers.
        Assert.Equal("aspire-traefik", Assert.Single(response.Headers.GetValues("X-Served-By")));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("admin", "/", false)]
    [InlineData("api", "/admin", true)]
    [InlineData("traefik", "/dashboard/", true)]
    public async Task BasicAuthenticationProtectsRoute(string host, string path, bool https)
    {
        using var client = CreateClient();
        var uri = new Uri(https ? Https(host, path) : Http(host, path));

        using (var anonymous = await client.GetAsync(uri))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using var signedIn = await SendAsync(client, HttpMethod.Get, uri, AdminCredentials);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task KeycloakProtectsRouteOverHttp()
    {
        using var client = CreateClient();

        using var response = await SignInThroughKeycloakAsync(client, new Uri(Http("sso")));

        Assert.Equal($"sso.{Domain}", response.RequestMessage!.RequestUri!.Host);
        Assert.Contains($"X-Auth-Request-Email: {SsoEmail}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task KeycloakProtectsRouteOverHttps()
    {
        using var client = CreateClient();

        using var response = await SignInThroughKeycloakAsync(client, new Uri(Https("secure")));

        Assert.Equal($"secure.{Domain}", response.RequestMessage!.RequestUri!.Host);
        Assert.Contains($"X-Auth-Request-Email: {SsoEmail}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task KeycloakSignInCoversOtherProtectedHosts()
    {
        using var client = CreateClient();
        (await SignInThroughKeycloakAsync(client, new Uri(Http("sso")))).Dispose();

        // Keycloak remembers the session, so the other hosts get through without a second sign-in form.
        using var secure = await GetFollowingRedirectsAsync(client, new Uri(Https("secure")));
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Equal($"secure.{Domain}", secure.RequestMessage!.RequestUri!.Host);

        var me = await ReadApiAsync(await GetFollowingRedirectsAsync(client, new Uri(Https("api", "/me"))));
        Assert.Equal("/me", me.GetProperty("path").GetString());
        Assert.Equal(SsoEmail, me.GetProperty("forwardedHeaders").GetProperty("X-Auth-Request-Email").GetString());
    }

    [Fact]
    public async Task ApiReportsItsPublicHttpsUrl()
    {
        using var client = CreateClient();

        var api = await ReadApiAsync(await client.GetAsync(Https("api")));

        Assert.Equal($"https://api.{Domain}:{playground.WebSecurePort}", api.GetProperty("publicUrl").GetString());
        Assert.Equal("https", api.GetProperty("forwardedHeaders").GetProperty("X-Forwarded-Proto").GetString());
    }

    [Theory]
    [InlineData("/v1/hello", "/v1/hello", null, null)]
    [InlineData("/api/", "/", "X-Forwarded-Prefix", "/api")]
    [InlineData("/forecast", "/reports/weather/daily-forecast", "X-Replaced-Path", "/forecast")]
    public async Task PathRouteForwardsExpectedPath(string publicPath, string apiPath, string? header, string? headerValue)
    {
        using var client = CreateClient();

        var api = await ReadApiAsync(await client.GetAsync(Http("app", publicPath)));

        Assert.Equal(apiPath, api.GetProperty("path").GetString());
        if (header is not null)
        {
            Assert.Equal(headerValue, api.GetProperty("forwardedHeaders").GetProperty(header).GetString());
        }
    }

    [Fact]
    public async Task TcpRouteReachesEchoServer()
    {
        // traefik/whoamitcp answers each line with "Received: <line>".
        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, playground.EchoTcpPort, cts.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync("hello\n"u8.ToArray(), cts.Token);
        using var reader = new StreamReader(stream);

        Assert.Equal("Received: hello", (await reader.ReadLineAsync(cts.Token))?.Trim());
    }

    [Fact]
    public async Task UdpRouteReachesEchoServer()
    {
        // traefik/whoamiudp answers each datagram with "Received: <datagram>". UDP may drop a datagram, and the fixture
        // only waits for the HTTP routes, so the datagram is sent again until an answer arrives.
        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var echoServer = new IPEndPoint(IPAddress.Loopback, playground.EchoUdpPort);

        while (true)
        {
            await udp.SendAsync("hello"u8.ToArray(), echoServer, cts.Token);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var result = await udp.ReceiveAsync(attempt.Token);

                Assert.Equal("Received: hello", Encoding.UTF8.GetString(result.Buffer).Trim());
                return;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                // No answer within this attempt; send the datagram again.
            }
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, AuthenticationHeaderValue? authorization = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = authorization;

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SignInThroughKeycloakAsync(HttpClient client, Uri protectedUri)
    {
        // Without a session, oauth2-proxy sends the browser to Keycloak's sign-in form.
        using var loginPage = await GetFollowingRedirectsAsync(client, protectedUri);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        Assert.Equal($"auth.{Domain}", loginPage.RequestMessage!.RequestUri!.Host);

        // Keycloak's sign-in page posts to a one-time URL, HTML-encoded in the form's action attribute:
        //   <form id="kc-form-login" class="..." onsubmit="..." action="https://auth.dev.localhost:8443/realms/traefik-demo/login-actions/authenticate?session_code=...&amp;execution=...&amp;client_id=oauth2-proxy&amp;tab_id=..." method="post">
        // The attribute order differs between Keycloak themes, so match the form tag first and the action within it.
        var form = LoginFormRegex().Match(await loginPage.Content.ReadAsStringAsync());
        Assert.True(form.Success, "Keycloak's sign-in form wasn't found.");
        var action = WebUtility.HtmlDecode(ActionRegex().Match(form.Value).Groups["action"].Value);

        using var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = SsoUser,
            ["password"] = SsoUser,
            ["credentialId"] = "",
        });

        var response = await FollowRedirectsAsync(client, await SendAsync(client, HttpMethod.Post, new Uri(loginPage.RequestMessage.RequestUri, action), content: credentials));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return response;
    }

    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(HttpClient client, Uri uri) =>
        await FollowRedirectsAsync(client, await client.GetAsync(uri));

    private static async Task<HttpResponseMessage> FollowRedirectsAsync(HttpClient client, HttpResponseMessage response)
    {
        // oauth2-proxy and Keycloak take a few redirects between them; more than ten means a loop.
        for (var i = 0; i < 10; i++)
        {
            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.MovedPermanently)
                || response.Headers.Location is not { } location)
            {
                return response;
            }

            var next = new Uri(response.RequestMessage!.RequestUri!, location);
            response.Dispose();
            response = await client.GetAsync(next);
        }

        throw new InvalidOperationException($"Too many redirects, last at {response.RequestMessage?.RequestUri}.");
    }

    private static async Task<JsonElement> ReadApiAsync(HttpResponseMessage response)
    {
        // Traefik.Api echoes the request as JSON:
        //   { "publicUrl": "https://api.dev.localhost:8443", "host": "...", "path": "/me", "forwardedHeaders": { "X-Forwarded-Proto": "https", ... } }
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return document.RootElement.Clone();
        }
    }

    [GeneratedRegex("""<form\b[^>]*\bid="kc-form-login"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LoginFormRegex();

    [GeneratedRegex(""""\baction="(?<action>[^"]+)"""", RegexOptions.IgnoreCase)]
    private static partial Regex ActionRegex();
}

/// <summary>
/// Starts <c>playground/traefik</c> once for all tests in <see cref="TraefikPlaygroundTests"/>, and stops it after the last.
/// </summary>
/// <remarks>
/// xUnit creates the fixture only when a test of the class runs, so filtering out the outerloop tests, or the
/// <c>category=failing</c> trait that <c>RequiresFeature</c> adds without a container runtime, never starts the app.
/// </remarks>
public sealed class TraefikPlaygroundFixture : IAsyncLifetime
{
    /// <summary>
    /// The domain of every route, from <c>Parameters:domain</c> in the playground's appsettings.json.
    /// </summary>
    public const string Domain = "dev.localhost";

    private DistributedApplication? _app;

    /// <summary>
    /// Gets the host port of Traefik's <c>web</c> entry point (HTTP).
    /// </summary>
    public int WebPort { get; private set; }

    /// <summary>
    /// Gets the host port of Traefik's <c>websecure</c> entry point (HTTPS).
    /// </summary>
    public int WebSecurePort { get; private set; }

    /// <summary>
    /// Gets the host port of the <c>echo-tcp</c> entry point.
    /// </summary>
    public int EchoTcpPort { get; private set; }

    /// <summary>
    /// Gets the host port of the <c>echo-udp</c> entry point.
    /// </summary>
    public int EchoUdpPort { get; private set; }

    /// <summary>
    /// Gets the password of the <c>admin</c> basic-authentication user.
    /// </summary>
    public string AdminPassword { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        // Not DistributedApplicationTestFactory: it pulls docker.io images from the CI mirror, which doesn't have the
        // traefik/* images, and gives parameters random values, while the routes need Parameters:domain to stay
        // dev.localhost. The playground's appsettings.json has a value for every parameter.
        // The playground pins Traefik's host ports for its README; random ports keep the tests from colliding with a
        // running copy. The AppHost reads these while it builds the model, so they must be arguments.
        string[] randomPorts = ["--Traefik:WebPort=", "--Traefik:WebSecurePort=", "--Traefik:EchoTcpPort=", "--Traefik:EchoUdpPort="];
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Traefik_AppHost>(randomPorts);

        // A fixture has no test output to write to; console logs end up in the test run's output.
        appHost.Services.AddLogging(logging => logging.AddSimpleConsole(console => console.SingleLine = true));

        Assert.Equal(Domain, appHost.Configuration["Parameters:domain"]);
        AdminPassword = appHost.Configuration["Parameters:admin-password"]!;

        _app = await appHost.BuildAsync();
        await _app.StartAsync();
        await _app.WaitForResources().WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        foreach (var resource in new[] { "keycloak", "sso-proxy", "whoami", "api" })
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(resource).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        }

        WebPort = _app.GetEndpoint("traefik", "web").Port;
        WebSecurePort = _app.GetEndpoint("traefik", "websecure").Port;
        EchoTcpPort = _app.GetEndpoint("traefik", "echo-tcp").Port;
        EchoUdpPort = _app.GetEndpoint("traefik", "echo-udp").Port;

        // Traefik picks up the routes, and whoami's health check, a moment after the resources are healthy.
        using var client = CreateClient();
        await WaitForSuccessAsync(client, new Uri($"http://whoami.{Domain}:{WebPort}/"));
        await WaitForSuccessAsync(client, new Uri($"https://api.{Domain}:{WebSecurePort}/"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates an HTTP client that sends every request to Traefik, with its own cookies, so tests don't share a sign-in.
    /// </summary>
    /// <returns>The client. Redirects aren't followed automatically.</returns>
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed by hand: HttpClient won't follow one from HTTPS to HTTP, which Keycloak sends back to
            // the plain HTTP sso.<domain> route.
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer(),
            // *.dev.localhost names aren't resolved everywhere. Every request goes to Traefik on the loopback address;
            // the URL's host still becomes the Host header and the TLS server name, which Traefik routes on.
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            // Traefik serves the developer certificate, or its self-signed default where there is none, as on CI agents.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static async Task WaitForSuccessAsync(HttpClient client, Uri uri)
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(uri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Traefik can reset a connection while it reloads its configuration, or a backend can still be starting.
                // Both are retried like an error status; the token source bounds the total wait.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }
    }
}
