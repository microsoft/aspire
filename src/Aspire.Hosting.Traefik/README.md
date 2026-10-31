# Traefik hosting integration

Use this integration to model, configure, and orchestrate a Traefik Proxy reverse proxy that routes traffic to the resources of an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Traefik` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Traefik
```

## Usage example

Then, in the AppHost, add a Traefik resource and route a host name to another resource with either C# or TypeScript:

**C#**

```csharp
var traefik = builder.AddTraefik("traefik")
                     .WithDashboard();

var api = builder.AddProject<Projects.Api>("api")
                 .WithTraefikRoute(traefik, "api.dev.localhost");
```

**TypeScript**

```typescript
const traefik = await builder.addTraefik("traefik")
    .withDashboard();

const api = await builder.addNodeApp("api", "../api", "server.js")
    .withTraefikRoute(traefik, "api.dev.localhost");
```

Browsers resolve every `*.localhost` name to the local machine, so `http://api.dev.localhost:<port>` works without editing the hosts file. The routed resource shows that URL in the dashboard.

## How routes are applied

Routes are declared on the routed resource and work the same way in both modes:

- **Run mode**: Traefik runs as a container. The routes are written to a file-provider configuration inside the container, and backends are reached from the container network, both containers and processes on the host (projects, executables). Traefik's traces and metrics are sent to the Aspire dashboard.
- **Publish (Docker Compose)**: the routes become `traefik.*` labels on the routed services. Traefik discovers them through its Docker provider, with the Docker socket mounted read-only. Parameter values in host names stay `${VAR}` placeholders in the generated `.env` file. When the compose environment deploys its Aspire dashboard (the default), Traefik sends its traces and metrics there.

Use the overload with a callback for path-based routes, priorities, or a specific endpoint:

```csharp
var domain = builder.AddParameter("domain");

api.WithTraefikRoute(traefik, route =>
{
    route.Host = ReferenceExpression.Create($"app.{domain}");
    route.PathPrefix = "/api";
    route.StripPathPrefix = true;   // the backend sees /orders for /api/orders
    route.EndpointName = "http";
});
```

### Rules

The matcher options of a route are combined with `&&`: `Host`, `PathPrefix`, `Path`, `Methods` and `ClientIps` (any of them), `Headers`, and `Query`. For alternatives, negations, or regular expressions, set `Rule` to a `TraefikRule` (in the `Aspire.Hosting.Traefik` namespace), built with `&`, `|`, and `!`:

```csharp
api.WithTraefikRoute(traefik, route =>
{
    route.Host = ReferenceExpression.Create($"api.{domain}");
    route.Rule = (TraefikRule.PathPrefix("/admin") | TraefikRule.PathRegexp("^/ops/[0-9]+"))
               & !TraefikRule.ClientIP("203.0.113.0/24");
});
// Host(`api.${DOMAIN}`) && (PathPrefix(`/admin`) || PathRegexp(`^/ops/[0-9]+`)) && !ClientIP(`203.0.113.0/24`)
```

Values are quoted and nested combinations get parentheses. `RawRule` takes a whole rule in Traefik's syntax instead, and can't be combined with the other options except `Host`, which then only sets the route's public URL and isn't added to the rule. See [rules and priority](https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/).

### Load balancing and health checks

A route's options also configure the Traefik service behind it:

```csharp
api.WithHttpProbe(ProbeType.Readiness, "/health")   // also Traefik's health check
   .WithTraefikRoute(traefik, route =>
   {
       route.Host = ReferenceExpression.Create($"api.{domain}");
       route.StickySession = new TraefikStickySessionOptions { CookieName = "affinity", Secure = true };
       route.PassHostHeader = false;
   });
```

- **Health checks**: an HTTP readiness probe (or else a liveness probe) of the routed endpoint, added with `WithHttpProbe`, becomes Traefik's active health check, with the probe's period and timeout. Traefik stops sending requests to a server while its check fails. Set `route.HealthCheck` for a check of its own, or `route.UseResourceProbe = false` to have none.
- **Sticky sessions**: `route.StickySession` keeps a client on one replica with a cookie.
- **HTTP/2 without TLS (h2c)**: an `http` endpoint whose transport is `http2`, such as a gRPC service with HTTP/2-only Kestrel endpoints, is called with h2c. For browsers, `AddGrpcWeb` adds a middleware that converts gRPC-Web into gRPC.
- **`PassHostHeader`**: `false` sends the backend's own address as the `Host` header instead of the client's.

See [load balancing](https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/).

### TCP and UDP routes

Protocols other than HTTP go through entry points of their own:

```csharp
var traefik = builder.AddTraefik("traefik")
    .WithEntryPoint("postgres", targetPort: 5432)
    .WithEntryPoint("dns", targetPort: 53, scheme: "udp");

postgres.WithTraefikTcpRoute(traefik, "postgres");   // every connection on the entry point
dns.WithTraefikUdpRoute(traefik, "dns");             // every datagram on the entry point
```

Plain TCP carries no host name, so such a route takes the whole entry point. Over TLS, Traefik reads the server name (SNI), so TCP routes can share an entry point, also with HTTPS routes. Traefik either terminates TLS, with the same certificates as HTTP routes, or passes the encrypted connection through:

```csharp
rabbit.WithTraefikTcpRoute(traefik, route =>
{
    route.EntryPoint = TraefikResource.WebSecureEntryPointName;
    route.HostSni = ReferenceExpression.Create($"amqp.{domain}");
    route.Tls = true;   // or route.TlsPassthrough = true
});
```

The routed endpoint must use the same protocol as the route: a UDP route needs an endpoint created with `protocol: ProtocolType.Udp`. A UDP entry point isn't proxied by Aspire in run mode, so its host port goes straight to Traefik. See [TCP routers](https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/router/) and [UDP routers](https://doc.traefik.io/traefik/reference/routing-configuration/udp/routing/router/).

## Middlewares

Traefik middlewares change requests or responses on a route, for example by adding headers or rewriting the path. The common ones have their own methods:

```csharp
var defaults = traefik.AddChain("defaults",
    traefik.AddSecurityHeaders("security"),          // HSTS, X-Frame-Options, nosniff, Referrer-Policy
    traefik.AddCompress("compress"));

var cors = traefik.AddHeaders("cors", headers =>
{
    headers.AccessControlAllowOriginList.Add("https://app.example.com");
    headers.AccessControlAllowMethods.Add("GET");
});

api.WithTraefikMiddleware(defaults, cors, traefik.AddRateLimit("rate", average: 100, burst: 50));
```

| Method | Traefik middleware |
| --- | --- |
| `AddStripPrefix`, `AddStripPrefixRegex`, `AddAddPrefix`, `AddReplacePath`, `AddReplacePathRegex` | Path rewrites |
| `AddRedirectScheme`, `AddRedirectRegex` | Redirects |
| `AddHeaders`, `AddSecurityHeaders` | `headers` (custom headers, CORS, security headers) |
| `AddBasicAuth` | `basicAuth` |
| `AddIpAllowList`, `AddRateLimit`, `AddInFlightRequestLimit` | Access and load limits |
| `AddCompress`, `AddRetry`, `AddCircuitBreaker`, `AddBuffering`, `AddErrorPages` | Responses and resilience |
| `AddForwardAuth`, `AddOAuth2ProxyForwardAuth`, `AddKeycloakForwardAuth` | `forwardAuth` |
| `AddChain` | `chain` |

### Basic authentication

`AddBasicAuth` takes one parameter per user, each holding an `htpasswd` entry (`htpasswd -nbB admin <password>`). This works in run mode and when published, and the plain password stays out of the deployment. Docker Compose interpolates `$` in `.env` values, which bcrypt hashes contain, so write the value in single quotes there: `ADMIN_HTPASSWD='admin:$2y$05$...'`.

For local development, `AddBasicAuth(name, user, passwordParameter)` takes a plain password and hashes it with SHA1. It throws when publishing.

### Forward authentication

`AddForwardAuth` asks an authentication server whether each request may pass: a `2xx` answer lets it through, anything else (for example a redirect to a sign-in page) goes back to the client. The server can be a resource of the application; its address is resolved from Traefik's point of view in run mode and becomes the service address when published.

```csharp
var authServer = builder.AddProject<Projects.AuthServer>("auth-server");

var check = traefik.AddForwardAuth("check", authServer.GetEndpoint("http"), "/verify",
    options => options.AuthResponseHeaders.Add("X-User"));

api.WithTraefikMiddleware(check);
```

### Single sign-on with OpenID Connect

`AddOAuth2ProxyForwardAuth` adds an [oauth2-proxy](https://oauth2-proxy.github.io/oauth2-proxy/) container that signs users in with any OpenID Connect provider. `AddKeycloakForwardAuth` does the same for a Keycloak resource routed through Traefik:

```csharp
var clientSecret = builder.AddParameter("sso-client-secret", secret: true);

var keycloak = builder.AddKeycloak("keycloak")
    .WithoutHttpsCertificate()
    .WithRealmImport("./Realms")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"auth.{domain}"));

var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, realm: "demo", clientId: "oauth2-proxy", clientSecret);

builder.AddProject<Projects.Admin>("admin")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"admin.{domain}"))
    .WithTraefikMiddleware(sso);
```

- Users without a session are redirected to the provider. Every protected host gets a route for `/oauth2/*` to oauth2-proxy, which receives the sign-in callback.
- Register `<public URL of each protected host>/oauth2/callback` as a redirect URI with the provider; `GetTraefikUrl()` builds it. Keycloak clients also need an audience mapper for the client ID.
- When a protected application has its own `/oauth2` paths, move oauth2-proxy's pages with `options.ProxyPrefix = "/sso"`; the callback is then `<public URL>/sso/callback`.
- The session cookie is `Secure` unless a protected route uses plain HTTP. `CookieSecure`, `CookieName`, and `CookieDomains` (for example `.example.com`, to sign in once for every subdomain) override that. `AdditionalArguments` passes any other [oauth2-proxy option](https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview), such as `--session-store-type=redis`; pass secrets through the environment instead, because arguments are visible in the dashboard and the published files.
- The backend receives the user in `X-Auth-Request-User`, `X-Auth-Request-Email`, `X-Auth-Request-Preferred-Username`, and `X-Auth-Request-Groups`.
- `AddKeycloakForwardAuth` sets `KC_HOSTNAME` to Keycloak's public URL (plus `KC_HOSTNAME_BACKCHANNEL_DYNAMIC`, `KC_PROXY_HEADERS`, and `KC_HTTP_ENABLED`), so tokens carry the public issuer while oauth2-proxy talks to Keycloak's internal endpoint. The package doesn't reference the Keycloak integration; any resource with endpoints can be passed.

### Error pages

`AddErrorPages` replaces error responses with pages from another resource that has a route through the same Traefik:

```csharp
var pages = builder.AddContainer("error-pages", "nginx")
    .WithHttpEndpoint(targetPort: 80)
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"errors.{domain}"));

api.WithTraefikMiddleware(traefik.AddErrorPages("errors", ["500-599"], pages, "/{status}.html"));
```

### Other middlewares

Any other middleware can be added by its Traefik type name and options:

```csharp
var headers = traefik.AddMiddleware("security-headers", "headers")
    .WithOption("frameDeny", true)
    .WithOption("customRequestHeaders.X-Script-Name", "test");

var strip = traefik.AddMiddleware("strip-v1", "stripPrefix")
    .WithOption("prefixes", ["/v1"]);

api.WithTraefikMiddleware(headers)                                    // every route of api
   .WithTraefikRoute(traefik, route =>
   {
       route.Host = ReferenceExpression.Create($"app.{domain}");
       route.PathPrefix = "/v1";
       route.Middlewares.Add(strip.Resource);                         // this route only
   });
```

- Option paths use dots for nested options, as in Traefik's label syntax. Values can be strings, booleans, numbers, lists, parameters, or expressions; at publish time parameters become `${VAR}` placeholders.
- Middlewares added with `WithTraefikMiddleware` run first, then the route's own, in the order they were added. They also apply to routes added later.
- In run mode middlewares are written to Traefik's dynamic configuration. At publish time they become `traefik.http.middlewares.*` labels on each service that uses them, so they also work with an external Traefik.
- Middlewares appear under Traefik in the dashboard.

See the [middleware reference](https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/) for the types and their options.

### Entry-point middlewares

`WithEntryPointMiddleware` applies middlewares to every router on an entry point, including routers that don't come from Aspire routes. They run before the middlewares of the route:

```csharp
traefik.WithEntryPointMiddleware(TraefikResource.WebSecureEntryPointName, traefik.AddSecurityHeaders("security-headers"));
```

- Traefik reads them from its static configuration, so they are defined in Traefik's own file provider. At publish time that file is a Docker Compose config of the Traefik service, with `${VAR}` placeholders like the labels.
- They can't be used with `PublishAsExternal`, which throws at publish time: configure them on the external Traefik instead.
- oauth2-proxy middlewares can't be used, because they would also intercept the sign-in callback, and neither can the `traefik` entry point, which serves the health check.

## Traefik dashboard

`WithDashboard()` enables the dashboard. In run mode it is served without authentication on the `traefik` endpoint and linked from the resource; at publish time it is enabled but not exposed.

To expose it, route it on a host behind at least one middleware:

```csharp
var htpasswd = builder.AddParameter("dashboard-htpasswd", secret: true);

traefik.WithDashboard(ReferenceExpression.Create($"traefik.{domain}"), traefik.AddBasicAuth("dashboard-auth", htpasswd));
```

The dashboard is then served at `/dashboard/` on that host, on the default entry point of the routes and with TLS when `WithLetsEncrypt` is used. Single sign-on middlewares work too: the host gets a sign-in route. With `PublishAsExternal`, nothing is published for the dashboard, because the external Traefik has its own.

## Public URLs

`GetTraefikUrl()` returns the address a resource is reachable on through Traefik. In run mode the URL includes Traefik's host port, so it is correct even with randomly assigned ports. Use it for OAuth redirect URIs or public base URLs:

```csharp
web.WithEnvironment("PUBLIC_URL", web.GetTraefikUrl());
```

The scheme and port follow Traefik's final configuration, so `WithLetsEncrypt` and `PublishAsExternal` can be called before or after `GetTraefikUrl()`.

At publish time the URL includes a port only when the entry point isn't on the standard port of its scheme (80 or 443), for example after `WithHostHttpsPort(8443)`, or with an external entry point described with a port. The Docker Compose publisher writes that port when the URL is built, so configure the ports before calling `GetTraefikUrl()`; a URL whose port changed afterwards throws when the files are written.

## HTTPS with Let's Encrypt

```csharp
var email = builder.AddParameter("acme-email");

var traefik = builder.AddTraefik("traefik")
                     .WithLetsEncrypt(email)
                     .WithHttpsRedirect();
```

This adds the `websecure` entry point (port 443), with TLS by default for every router on it. At publish time, routes request certificates from Let's Encrypt (TLS-ALPN-01 by default, or HTTP-01), which are stored in a volume. Let's Encrypt can't issue certificates for a local run, because it must reach the machine on public ports 80 and 443 under a public DNS name, so in run mode Traefik serves HTTPS with the certificate Aspire gives it: the ASP.NET Core developer certificate, which covers `localhost` and `*.dev.localhost` and which browsers trust once it's trusted with `dotnet dev-certs https --trust`. `WithHttpsCertificate` sets another certificate, and `WithoutHttpsCertificate` keeps Traefik's self-signed default. A certificate whose key needs a password isn't supported, because Traefik reads PEM files only. Pass `staging: true` while testing a deployment to avoid Let's Encrypt rate limits.

The email can be a string, a parameter, or an expression such as `ReferenceExpression.Create($"admin@{domain}")`. To choose the ACME directory per deployment, or to use another ACME certificate authority, pass it as a parameter instead of `staging`:

```csharp
var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");

traefik.WithLetsEncrypt(email)
       .WithAcmeCaServer(caServer);
```

### DNS-01 and wildcard certificates

`WithDnsChallenge` proves control of the domains through a DNS provider's API instead. It works for hosts that the internet can't reach, and it is the only way to get a wildcard certificate, which `WithTlsDomain` requests:

```csharp
var token = builder.AddParameter("cloudflare-token", secret: true);

traefik.WithLetsEncrypt(email)
       .WithDnsChallenge("cloudflare", dns => dns.Credentials["CF_DNS_API_TOKEN"] = token)
       .WithTlsDomain(ReferenceExpression.Create($"{domain}"), ReferenceExpression.Create($"*.{domain}"));
```

- The provider names and their credential variables are listed in the [lego documentation](https://go-acme.github.io/lego/dns/). The credentials become environment variables of Traefik when publishing; `aspire run` doesn't request certificates, so it doesn't ask for them.
- `dns.Resolvers` sets the DNS servers used to check propagation (`1.1.1.1:53`), and `dns.DelayBeforeChecks` how long to wait first.
- With TLS domains, the `websecure` entry point requests one certificate per domain at publish time, and routes whose host it covers use it.

### Several resolvers

Calling `WithLetsEncrypt` again with another `resolverName` adds a resolver. The first one is the default; a route chooses another with `CertResolver`:

```csharp
traefik.WithLetsEncrypt(email)                                                    // default: DNS-01 above
       .WithLetsEncrypt(email, resolverName: "http", challenge: TraefikAcmeChallenge.Http);

partner.WithTraefikRoute(traefik, route =>
{
    route.Host = ReferenceExpression.Create($"shop.example.org");
    route.CertResolver = "http";
});
```

## Using a Traefik that already runs on the server

When the deployment target already runs Traefik with the Docker provider, use the local container only for development:

```csharp
var traefik = builder.AddTraefik("traefik")
                     .PublishAsExternal(network: "traefik", entryPoint: "websecure", certResolver: "letsencrypt");
```

At publish time no Traefik service is generated. The routed services get labels for the existing Traefik, join the `traefik` network, and the network is declared as external.

When servers name their entry point or resolver differently, pass them as expressions; parameters become `${VAR}` placeholders in the labels. The network name must be a literal, because Docker Compose doesn't substitute variables in YAML keys.

```csharp
var entryPoint = builder.AddParameter("traefik-entrypoint");

traefik.PublishAsExternal("traefik", ReferenceExpression.Create($"{entryPoint}"));
```

Aspire can't see the external Traefik's configuration, so by default it assumes that an entry point named `web` is plain HTTP, any other one is HTTPS, and both are on the standard ports. That decides the scheme of public URLs and whether the oauth2-proxy cookie is `Secure`. Describe the entry points that don't follow it:

```csharp
traefik.PublishAsExternal(network: "proxy", entryPoint: "https")
       .WithExternalEntryPoint("http", isTls: false)
       .WithExternalEntryPoint("https", isTls: true, port: 8443);
```

## Other settings

- `WithHostPort` / `WithHostHttpsPort` pin the host ports (random in run mode, 80 and 443 at publish time by default).
- `WithEntryPoint` adds more entry points, for example for TCP traffic. An entry point with the `https` scheme terminates TLS for its HTTP routes like `websecure`: with the developer certificate in run mode, and at publish time with the default Let's Encrypt resolver, or Traefik's self-signed certificate when there is none.
- `WithDashboard` enables the Traefik dashboard; see [Traefik dashboard](#traefik-dashboard).
- `WithLogLevel(TraefikLogLevel.Debug)` sets the level of Traefik's logs (errors only by default).
- `WithAccessLogs(logs => logs.StatusCodes.Add("400-599"))` logs requests, in the common or JSON format, optionally filtered by status code, retries, or duration. Request headers are dropped unless listed in `KeepHeaders`. Both logs are Traefik's console output, so they show up in the Aspire dashboard and in `docker compose logs`.
- Traefik doesn't check for new versions or send usage statistics; the AppHost pins its image version.
- Traefik reads its static configuration from command-line arguments, so `WithArgs(...)` adds any other setting.

## Security considerations

At publish time the managed Traefik mounts `/var/run/docker.sock` read-only so the Docker provider can read labels. Rootless Docker and Podman use another socket path on the host; set it with `WithDockerSocketPath("/run/user/1000/docker.sock")`. Access to the Docker socket is powerful even when read-only. For internet-facing deployments, use `WithDockerSocketProxy()`: a [docker-socket-proxy](https://github.com/Tecnativa/docker-socket-proxy) service mounts the socket instead and lets Traefik read containers and events only. The proxy has no authentication, so other services on the application's network can read the container list too. The proxy image comes from a third party and is pinned by digest; to use a mirror or another proxy, replace the image in the callback: `WithDockerSocketProxy(proxy => proxy.WithImageRegistry(...).WithImage(...))`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://doc.traefik.io/traefik/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Traefik* is a trademark of *Traefik Labs*._
