// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// All public host names hang off one parameter, so the same AppHost publishes with a ${DOMAIN} placeholder.
// Browsers resolve *.localhost to loopback, so dev.localhost needs no hosts-file entries.
var domain = builder.AddParameter("domain");

builder.AddDockerComposeEnvironment("compose");

var traefik = builder.AddTraefik("traefik")
    .WithDashboard()
    // When published, Traefik reads the Docker API through a filtering proxy instead of mounting the socket.
    .WithDockerSocketProxy()
    // Failed requests only; traces in the Aspire dashboard cover the rest.
    .WithAccessLogs(logs => logs.StatusCodes.Add("400-599"));

if (builder.ExecutionContext.IsRunMode)
{
    // Pin the host ports so the URLs in the README stay stable. Nothing below depends on them: public URLs come
    // from GetTraefikUrl(), which includes whatever port is allocated.
    traefik
        .WithHostPort(builder.Configuration.GetValue<int?>("Traefik:WebPort"))
        .WithHostHttpsPort(builder.Configuration.GetValue<int?>("Traefik:WebSecurePort"))
        // Show the websecure entry point as https://traefik.<domain>:<port>, the host the dashboard is routed on. Aspire
        // would otherwise derive traefik-traefik.dev.localhost from the AppHost's own URL. Only the displayed URL
        // changes: the endpoint's TargetHost is the IP Docker publishes on, so it can't be a host name.
        .WithUrls(async context =>
        {
            if (context.Urls.FirstOrDefault(u => u.Endpoint?.EndpointName == TraefikResource.WebSecureEntryPointName) is { } url)
            {
                var host = $"traefik.{await domain.Resource.GetValueAsync(context.CancellationToken)}";
                url.Url = new UriBuilder(url.Url) { Host = host }.Uri.ToString().TrimEnd('/');
            }
        });
}

// HTTPS on the websecure entry point. In run mode Traefik serves the Aspire developer certificate; Let's Encrypt is
// only used when published. Once a resolver exists, websecure is the default entry point of every route, so routes
// that should stay on plain HTTP set EntryPoint to `httpOnly` (see below).
traefik.WithLetsEncrypt(builder.AddParameter("acme-email"));

// Traefik:EnableHttps moves every route to HTTPS, and redirects HTTP to it.
var enableHttps = builder.Configuration.GetValue<bool>("Traefik:EnableHttps");
var httpOnly = enableHttps ? null : TraefikResource.WebEntryPointName;
if (enableHttps)
{
    traefik.WithHttpsRedirect();
}

// Optionally one wildcard certificate for *.<domain>, through the DNS-01 challenge, instead of one certificate per
// host. For example Traefik:DnsProvider=cloudflare and Traefik:DnsCredentialName=CF_DNS_API_TOKEN; the credential
// is only asked for when publishing.
if (builder.Configuration["Traefik:DnsProvider"] is { Length: > 0 } dnsProvider)
{
    var dnsCredentialName = builder.Configuration["Traefik:DnsCredentialName"]
        ?? throw new InvalidOperationException("Set Traefik:DnsCredentialName to the provider's credential variable.");
    var dnsCredential = builder.AddParameter("dns-credential", secret: true);

    traefik
        .WithDnsChallenge(dnsProvider, dns => dns.Credentials[dnsCredentialName] = dnsCredential)
        .WithTlsDomain(ReferenceExpression.Create($"{domain}"), ReferenceExpression.Create($"*.{domain}"));
}

// Middlewares shared by several routes: security headers and compression, applied as one chain.
var defaults = traefik.AddChain("defaults",
    traefik.AddSecurityHeaders("security-headers"),
    traefik.AddCompress("compress"));

// Basic authentication. A plain password can only be hashed in run mode; a published app takes an htpasswd entry
// (`htpasswd -nbB admin <password>`), which also keeps the password out of the deployment.
var adminAuth = builder.ExecutionContext.IsPublishMode
    ? traefik.AddBasicAuth("admin-auth", builder.AddParameter("admin-htpasswd", secret: true))
    : traefik.AddBasicAuth("admin-auth", "admin", builder.AddParameter("admin-password", secret: true));

// The dashboard on https://traefik.<domain>, behind the same basic authentication. In run mode it is also served
// without authentication on the traefik endpoint.
traefik.WithDashboard(ReferenceExpression.Create($"traefik.{domain}"), adminAuth);

// An entry-point middleware applies to every router on the entry point, including the dashboard and sign-in routes.
var servedBy = traefik.AddHeaders("served-by", headers => headers.CustomResponseHeaders["X-Served-By"] = "aspire-traefik");
traefik
    .WithEntryPointMiddleware(TraefikResource.WebEntryPointName, servedBy)
    .WithEntryPointMiddleware(TraefikResource.WebSecureEntryPointName, servedBy);

// Single sign-on: Keycloak behind Traefik at auth.<domain>. AddKeycloakForwardAuth runs oauth2-proxy between Traefik
// and Keycloak, and sets Keycloak's hostname to its public URL. Every route that uses `sso` sends users to Keycloak
// first; the realm's users are in Realms/traefik-demo-realm.json.
var clientSecret = builder.AddParameter("sso-client-secret", secret: true);

#pragma warning disable ASPIRECERTIFICATES001 // WithoutHttpsCertificate is experimental.
// Fixed admin credentials for the Keycloak admin console (master realm), from Parameters in appsettings.json.
var keycloak = builder.AddKeycloak("keycloak",
        adminUsername: builder.AddParameter("keycloak-admin"),
        adminPassword: builder.AddParameter("keycloak-password", secret: true))
    // Plain HTTP inside the application: Traefik is the only entry point.
    .WithoutHttpsCertificate()
    .WithRealmImport("./Realms")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"auth.{domain}"));
#pragma warning restore ASPIRECERTIFICATES001

var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, realm: "traefik-demo", clientId: "oauth2-proxy", clientSecret);

// A container backend, reached by its name on the container network, behind four routes that differ only in how
// they are protected. In run mode its own port is also published on localhost, which bypasses Traefik; when
// published, only Traefik can reach it.
var whoami = builder.AddContainer("whoami", "traefik/whoami", "v1.11")
    .WithHttpEndpoint(targetPort: 80)
    // Traefik takes its health check from the readiness probe, and stops routing to whoami while it fails.
    .WithHttpProbe(ProbeType.Readiness, "/health")
    .WithTraefikRoute(traefik, route =>
    {
        // Open, over plain HTTP.
        route.Host = ReferenceExpression.Create($"whoami.{domain}");
        route.EntryPoint = httpOnly;
    })
    .WithTraefikRoute(traefik, route =>
    {
        // Basic authentication, over plain HTTP.
        route.Host = ReferenceExpression.Create($"admin.{domain}");
        route.EntryPoint = httpOnly;
        route.Middlewares.Add(adminAuth.Resource);
    })
    .WithTraefikRoute(traefik, route =>
    {
        // Keycloak, over plain HTTP. With one plain HTTP route, oauth2-proxy's session cookie isn't marked Secure,
        // since browsers don't send Secure cookies over HTTP.
        route.Host = ReferenceExpression.Create($"sso.{domain}");
        route.EntryPoint = httpOnly;
        route.RouterName = "whoami-sso";
        route.Middlewares.Add(sso.Resource);
    })
    .WithTraefikRoute(traefik, route =>
    {
        // Keycloak, over HTTPS: websecure is the default entry point.
        route.Host = ReferenceExpression.Create($"secure.{domain}");
        route.RouterName = "whoami-secure";
        route.Middlewares.Add(sso.Resource);
    })
    .WithTraefikMiddleware(defaults);

// TCP and UDP: Traefik's echo servers on their own entry points, for protocols other than HTTP.
traefik
    .WithEntryPoint("echo-tcp", targetPort: 9000, port: builder.Configuration.GetValue<int?>("Traefik:EchoTcpPort"))
    .WithEntryPoint("echo-udp", targetPort: 9001, port: builder.Configuration.GetValue<int?>("Traefik:EchoUdpPort"), scheme: "udp");

builder.AddContainer("echo-tcp", "traefik/whoamitcp", "v0.3.0")
    .WithEndpoint(targetPort: 8080, name: "tcp")
    .WithTraefikTcpRoute(traefik, "echo-tcp");

builder.AddContainer("echo-udp", "traefik/whoamiudp", "v0.2.3")
    .WithEndpoint(targetPort: 8080, name: "udp", protocol: ProtocolType.Udp)
    .WithTraefikUdpRoute(traefik, "echo-udp");

// A project backend, running on the host and reached from the Traefik container: api.<domain> over HTTPS, and
// app.<domain> over plain HTTP.
var api = builder.AddProject<Projects.Traefik_Api>("api")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
    .WithTraefikRoute(traefik, route =>
    {
        // Sub-routes on the same host, each behind its own authentication; the rest of api.<domain> stays open.
        // Traefik prefers the longer rule, so Host && PathPrefix wins over the bare Host route above.
        route.Host = ReferenceExpression.Create($"api.{domain}");
        route.PathPrefix = "/admin";
        route.RouterName = "api-admin";
        route.Middlewares.Add(adminAuth.Resource);
    })
    .WithTraefikRoute(traefik, route =>
    {
        // Signing in sends the browser back to api.<domain>/oauth2/callback, which Keycloak must allow (see below).
        route.Host = ReferenceExpression.Create($"api.{domain}");
        route.PathPrefix = "/me";
        route.RouterName = "api-me";
        route.Middlewares.Add(sso.Resource);
    })
    .WithTraefikRoute(traefik, route =>
    {
        // A short public path for a long internal one: app.<domain>/forecast reaches /reports/weather/daily-forecast.
        // The API sees the long path; Traefik keeps the original in X-Replaced-Path.
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.Path = "/forecast";
        route.RouterName = "api-forecast";
        route.EntryPoint = httpOnly;
        route.Middlewares.Add(traefik.AddReplacePath("forecast-path", "/reports/weather/daily-forecast").Resource);
    })
    .WithTraefikRoute(traefik, route =>
    {
        // A second, path-based route to the same project: app.<domain>/v1/... (the prefix is forwarded as is).
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.PathPrefix = "/v1";
        route.RouterName = "api-v1";
        route.EntryPoint = httpOnly;
    })
    .WithTraefikRoute(traefik, route =>
    {
        // app.<domain>/api/... reaches the API without the prefix; Traefik passes it in X-Forwarded-Prefix.
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.PathPrefix = "/api";
        route.StripPathPrefix = true;
        route.RouterName = "api-stripped";
        route.EntryPoint = httpOnly;
    })
    .WithTraefikMiddleware(traefik.AddRateLimit("api-rate-limit", average: 20, burst: 40));

// The API reports its own public URL, as an OAuth redirect URI or a link in an email would need.
api.WithEnvironment("PUBLIC_URL", api.GetTraefikUrl());

// Placeholders resolved by Keycloak while importing Realms/traefik-demo-realm.json: every host protected by `sso`
// needs its callback registered. GetTraefikUrl(routerName) gives the URL of one route of a resource with several.
keycloak
    .WithEnvironment("TRAEFIK_DEMO_CLIENT_SECRET", clientSecret)
    .WithEnvironment("TRAEFIK_DEMO_HTTP_REDIRECT_URI", ReferenceExpression.Create($"{whoami.GetTraefikUrl("whoami-sso")}/oauth2/callback"))
    .WithEnvironment("TRAEFIK_DEMO_REDIRECT_URI", ReferenceExpression.Create($"{whoami.GetTraefikUrl("whoami-secure")}/oauth2/callback"))
    .WithEnvironment("TRAEFIK_DEMO_API_REDIRECT_URI", ReferenceExpression.Create($"{api.GetTraefikUrl()}/oauth2/callback"));

builder.Build().Run();
