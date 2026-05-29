# Aspire Dashboard: Reverse Proxy & Path-Base Deployment

## Overview

The Aspire Dashboard can be deployed behind a reverse proxy (e.g., Azure Front Door, YARP) at a sub-path like `/sandbox/us`. This document captures the required changes and lessons learned.

## Deployment Shape

- **URL**: `https://aspire.sandbox.elite.com/sandbox/us`
- **Infrastructure**: Azure Container Apps + Azure Front Door + Entra ID (OIDC)
- **Container Registry**: `h3econtainerregistry.azurecr.io/aspire-dashboard`
- **Ports**: 18888 (browser), 18889 (OTLP), 18890 (OTLP HTTP)

## Configuration

| Environment Variable | Value | Purpose |
|---------------------|-------|---------|
| `Dashboard__PathBase` | `/sandbox/us` | Sets the application path base |
| `ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED` | `true` | Enables `UseForwardedHeaders()` |

## Critical Middleware Order

The middleware pipeline in `DashboardWebApplication.cs` **must** follow this order:

```
1. UseForwardedHeaders()          — fixes scheme/host from proxy
2. PathBase middleware             — sets Request.PathBase from config or X-Forwarded-Prefix
3. UseRouting()                    — endpoint matching (MUST be after path-base)
4. ... other middleware ...
5. UseAuthentication()
6. UseAuthorization()
7. UseAntiforgery()
8. Map endpoints (RazorComponents, gRPC, etc.)
```

### Why `UseRouting()` Must Be After Path-Base

Without explicit `UseRouting()`, ASP.NET Core 8 implicitly places it at the very beginning of the pipeline — **before** the path-base middleware adjusts `Request.Path`. This causes endpoint matching to see the full path (e.g., `/sandbox/us/_blazor/negotiate`) and fail to match the SignalR hub endpoint. The request falls through to the Razor component catch-all, which rejects it with `Incorrect Content-Type:` because it expects an HTML GET, not a SignalR negotiate POST.

## Path-Base Middleware Logic

```csharp
var pathBase = builder.Configuration["Dashboard:PathBase"];
if (!string.IsNullOrEmpty(pathBase))
{
    var configuredPathBase = new PathString(pathBase.TrimEnd('/'));
    _app.Use((context, next) =>
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-Prefix", out var forwardedPrefix) && forwardedPrefix.Count > 0)
        {
            context.Request.PathBase = new PathString(forwardedPrefix.ToString().TrimEnd('/'));
        }
        else if (context.Request.Path.StartsWithSegments(configuredPathBase, out var remaining))
        {
            context.Request.PathBase = configuredPathBase;
            context.Request.Path = remaining;
        }
        else
        {
            context.Request.PathBase = configuredPathBase;
        }
        return next();
    });
}
```

## JS Module Imports — Must Be Relative

All JS module imports **must** use relative paths (`./`) so they resolve relative to the `<base href>` which includes the path base. Absolute paths (`/`) resolve to the domain root and bypass the path base entirely.

### Files That Were Fixed

| File | Old Path | New Path |
|------|----------|----------|
| `Components/Controls/Chart/PlotlyChart.razor.cs` | `/js/app-metrics.js` | `./js/app-metrics.js` |
| `Components/Controls/Chart/MetricTable.razor.cs` | `/Components/Controls/Chart/MetricTable.razor.js` | `./Components/Controls/Chart/MetricTable.razor.js` |
| `Components/Controls/MarkdownRenderer.razor.cs` | `/Components/Controls/MarkdownRenderer.razor.js` | `./Components/Controls/MarkdownRenderer.razor.js` |
| `Components/Controls/TextVisualizer.razor.cs` | `/Components/Controls/TextVisualizer.razor.js` | `./Components/Controls/TextVisualizer.razor.js` |
| `Components/Pages/Login.razor.cs` | `/Components/Pages/Login.razor.js` | `./Components/Pages/Login.razor.js` |
| `Components/Pages/Resources.razor.cs` | `/js/app-resourcegraph.js` | `./js/app-resourcegraph.js` |
| `Components/Controls/AssistantChat.razor.js` | `/js/highlight-11.11.1.min.js` | `../../js/highlight-11.11.1.min.js` |
| `Components/Controls/MarkdownRenderer.razor.js` | `/js/highlight-11.11.1.min.js` | `../../js/highlight-11.11.1.min.js` |
| `Components/Controls/TextVisualizer.razor.js` | `/js/highlight-11.11.1.min.js` | `../../js/highlight-11.11.1.min.js` |
| `Components/Layout/MainLayout.razor.cs` | `/js/app-theme.js` | `./js/app-theme.js` |
| `wwwroot/js/app-theme.js` | Absolute Fluent UI import | Relative (`../`) Fluent UI import |

### Rule

Any `JS.InvokeAsync<IJSObjectReference>("import", "...")` call must use `./` prefix, never `/`.

## Blazor Shell — Base Href

`Components/App.razor` sets the base href dynamically:

```html
<base href="@(Configuration["Dashboard:PathBase"] ?? "/")" />
```

This ensures all relative URLs (scripts, CSS, navigation) resolve under the configured path base.

## Home Button Links

`Components/Layout/MainLayout.razor` has logo/home links that must use `Href="./"` (relative) instead of `Href="/"` (absolute):

```razor
<FluentAnchor Appearance="Appearance.Stealth" Href="./" Class="logo">
```

There are 3 instances: the icon, the desktop header, and the mobile header.

## OIDC / Authentication

- **Callback path**: `/signin-oidc` (relative to path base, so the full URL is `/sandbox/us/signin-oidc`)
- The OIDC `OnRedirectToIdentityProvider` event must construct `redirect_uri` using the full path base
- `UseAuthentication()` must be explicitly in the pipeline
- Cookie paths are scoped to the path base automatically

## Build & Publish

```powershell
# Login to ACR
az acr login --name h3econtainerregistry

# Publish container image
dotnet publish src/Aspire.Dashboard/Aspire.Dashboard.csproj `
  -c Release -r linux-x64 `
  /p:PublishProfile=DefaultContainer `
  /p:ContainerImageTag=13.4.0-elite.10 `
  /p:ContainerRegistry=h3econtainerregistry.azurecr.io `
  /p:ContainerRepository=aspire-dashboard
```

## Local Development with YARP Proxy

Use `src/Aspire.ProxyAuth/appsettings.json` to simulate the reverse proxy locally:

- Route: `/sandbox/us/{**catch-all}`
- Transform: `PathRemovePrefix: /sandbox/us`
- Adds header: `X-Forwarded-Prefix: /sandbox/us`

Launch settings in `src/Aspire.Dashboard/Properties/launchSettings.json` include:
- `Dashboard__PathBase=/sandbox/us/`
- `ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED=true`

## Common Failure Modes

| Symptom | Cause | Fix |
|---------|-------|-----|
| `_blazor/negotiate` returns 500 with "Incorrect Content-Type" | `UseRouting()` before path-base middleware | Move `UseRouting()` after path-base handling |
| JS module 404s (e.g., `/js/app-metrics.js`) | Absolute import paths | Change to relative `./` paths |
| Infinite auth redirect loop | Missing `UseAuthentication()` or wrong callback path | Add middleware, align callback with path base |
| Home button navigates to domain root | `Href="/"` hardcoded | Change to `Href="./"` |
| AADSTS50011 redirect URI mismatch | `redirect_uri` doesn't include path base | Fix OIDC event to prepend path base |
| Static assets 404 | `<base href>` not set to path base | Set `<base href="@(Configuration["Dashboard:PathBase"] ?? "/")" />` |

## Key Insight

`Dashboard__PathBase` was partially implemented upstream but never validated end-to-end. The config key and base middleware existed, but component code (JS imports, links) all used absolute root paths that broke under any non-root deployment.
