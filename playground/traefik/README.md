# Traefik playground

Demonstrates `Aspire.Hosting.Traefik`: host- and path-based routing to a container and to a project, public URLs, and Traefik telemetry in the Aspire dashboard.

## Run

Prerequisites: Docker, and ports 8000 and 8443 free (or change `Traefik:WebPort` / `Traefik:WebSecurePort` in `Traefik.AppHost/appsettings.json`).

```bash
cd playground/traefik
aspire run
```

## What to try

| URL | Backend | Shows |
| --- | --- | --- |
| `http://whoami.dev.localhost:8000` | `traefik/whoami` container | Routing to a container, open; the echoed request shows the `X-Forwarded-*` headers Traefik adds. |
| `http://admin.dev.localhost:8000` | `traefik/whoami` container | The same container behind basic authentication (`admin` / `admin`, from `Parameters:admin-password`). |
| `http://sso.dev.localhost:8000` | `traefik/whoami` container | The same container behind Keycloak single sign-on through oauth2-proxy, over plain HTTP. Sign in as `alice` / `alice`; the echoed request shows the `X-Auth-Request-*` headers. |
| `https://secure.dev.localhost:8443` | `traefik/whoami` container | The same container behind Keycloak single sign-on, over HTTPS. Keycloak remembers the sign-in, so after one Keycloak route the others don't ask for the password again. |
| `https://api.dev.localhost:8443` | `Traefik.Api` project | Routing to a process on the host, over HTTPS. The response includes `PUBLIC_URL`, computed with `GetTraefikUrl()`. |
| `http://app.dev.localhost:8000/v1/hello` | `Traefik.Api` project | A second, path-based route (`PathPrefix(/v1)`) to the same project. |
| `http://app.dev.localhost:8000/api/` | `Traefik.Api` project | `StripPathPrefix`: the API sees `/`, and `X-Forwarded-Prefix: /api`. |
| `https://api.dev.localhost:8443/admin` | `Traefik.Api` project | A sub-route behind basic authentication (`admin` / `admin`); the rest of `https://api.dev.localhost:8443` stays open. |
| `https://api.dev.localhost:8443/me` | `Traefik.Api` project | A sub-route behind Keycloak single sign-on (`alice` / `alice`); the response shows the `X-Auth-Request-*` headers. |
| `http://app.dev.localhost:8000/forecast` | `Traefik.Api` project | A short public path for a long one: `replacePath` sends `/reports/weather/daily-forecast`, and `X-Replaced-Path` keeps `/forecast`. |
| `https://auth.dev.localhost:8443` | Keycloak | The Keycloak realm `traefik-demo`, imported from `Traefik.AppHost/Realms`. The admin console at `/admin` signs in with `adminkc` / `adminkc` (`Parameters:keycloak-admin` and `Parameters:keycloak-password`). |
| Traefik resource → **Dashboard** link | Traefik API | The Traefik dashboard with the generated routers and services. |
| `https://traefik.dev.localhost:8443/dashboard/` | Traefik API | The same dashboard, routed on a host behind basic authentication (`admin` / `admin`). |
| `127.0.0.1:9000` (TCP) | `traefik/whoamitcp` container | A TCP route on the `echo-tcp` entry point: send a line, for example with `ncat 127.0.0.1 9000`, and it comes back as `Received: ...`. |
| `127.0.0.1:9001` (UDP) | `traefik/whoamiudp` container | A UDP route on the `echo-udp` entry point: `ncat -u 127.0.0.1 9001` echoes datagrams the same way. |

Middlewares:

- `whoami` (all four routes) uses the `defaults` chain: security headers (`X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`; HSTS on HTTPS only) and compression. Check the response headers in the browser's developer tools.
- Every router on the `web` and `websecure` entry points gets the `served-by` entry-point middleware, which adds `X-Served-By: aspire-traefik` to responses.
- `api` (all routes) is rate-limited to 20 requests per second with bursts of 40; more get `429`.
- The middlewares appear under `traefik` in the Aspire dashboard and in the Traefik dashboard.

Health checks: `whoami` has a readiness probe on `/health`, which Traefik also uses as its health check. `curl -X POST -d 500 -H "Host: whoami.dev.localhost" http://127.0.0.1:8000/health` makes whoami report unhealthy; a few seconds later Traefik answers `503` for it. Traefik no longer forwards to it then, so post `200` to whoami's direct `localhost` URL, shown in its resource details, to bring it back. Note that the probe also marks the resource unhealthy in the Aspire dashboard.

In the Aspire dashboard:

- The `whoami` and `api` resources show their public URLs; the direct `localhost` URLs are in the resource details. Those direct ports exist only in run mode and skip Traefik and its authentication; when published, only Traefik can reach `whoami`.
- The `traefik` resource shows its `websecure` entry point as `https://traefik.dev.localhost:8443`, the host the dashboard is routed on.
- **Traces**: a request to `api.dev.localhost` is one trace with a Traefik span and an `api` span.
- **Metrics**: select `traefik` for request counts and durations per entry point, router, and service.
- **Console logs**: `traefik` logs failed requests (status 400 to 599) as access logs; open a host without a route to see one.

Browsers resolve `*.localhost` to the local machine. Other tools may not; with curl, send the host explicitly:

```bash
curl -H "Host: api.dev.localhost" http://127.0.0.1:8000/
```

## HTTPS

The playground mixes HTTP and HTTPS. `WithLetsEncrypt` adds the `websecure` entry point and makes it the default, so `api`, `secure`, `auth`, and the routed dashboard use HTTPS on port 8443. The `whoami`, `admin`, `sso`, and `app` routes set `EntryPoint` to `web` and stay on HTTP on port 8000. Because `sso` is plain HTTP, oauth2-proxy's session cookie isn't marked `Secure`: browsers don't send `Secure` cookies over HTTP.

In run mode Traefik serves the ASP.NET Core developer certificate, which covers `*.dev.localhost`, so the browser trusts `https://api.dev.localhost:8443` once the certificate is trusted (`dotnet dev-certs https --trust`). Let's Encrypt is only used when published.

Set `Traefik:EnableHttps` to `true` to move every route to HTTPS, with a redirect from HTTP.

To publish with one wildcard certificate for `*.${DOMAIN}` through the DNS-01 challenge, also set `Traefik:DnsProvider` (a [lego provider](https://go-acme.github.io/lego/dns/), for example `cloudflare`) and `Traefik:DnsCredentialName` (its credential variable, for example `CF_DNS_API_TOKEN`). The credential is the `DNS_CREDENTIAL` variable of the published `.env` file.

## Publish

```bash
aspire publish -o ./out
```

`out/docker-compose.yaml` contains a Traefik service using the Docker provider and `traefik.*` labels on `whoami` and `api`, with `${DOMAIN}` in the host rules. The middleware definitions are labels on the services that use them. Traefik's own configuration, the dashboard router on `traefik.${DOMAIN}` and the entry-point middleware, is the `traefik-dynamic` config. Traefik reads the Docker API through the `traefik-socket-proxy` service and doesn't mount the Docker socket itself.

When published, Keycloak uses `https://auth.${DOMAIN}`. The realm's redirect URIs, one per host protected by Keycloak, follow from `GetTraefikUrl()`, and from `GetTraefikUrl(routerName)` for the `whoami` routes.

When published, basic authentication takes an htpasswd entry in the `ADMIN_HTPASSWD` variable instead of a plain password. Create one with `htpasswd -nbB admin <password>`. Docker Compose interpolates `$` in `.env` values, so put it in single quotes there: `ADMIN_HTPASSWD='admin:$2y$05$...'`.
