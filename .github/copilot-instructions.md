# Copilot Instructions

## Aspire Dashboard — Reverse Proxy & Path-Base Deployment

This repository contains a customized Aspire Dashboard deployed behind a reverse proxy at a sub-path (`/sandbox/us`). See `docs/PROXY-PATHBASE-DEPLOYMENT.md` for full details.

### Critical Rules

1. **JS module imports must use relative paths** (`./`), never absolute (`/`). Absolute paths bypass the `<base href>` and break under any non-root path base.

2. **`UseRouting()` must be placed after the path-base middleware** in `DashboardWebApplication.cs`. Without this, endpoint matching sees the un-stripped path and Blazor SignalR negotiation fails.

3. **All `Href` attributes for internal navigation must use relative paths** (`./` or relative segments), not `/`.

4. **The `<base href>` in `App.razor`** is set from `Configuration["Dashboard:PathBase"]` — all relative resolution depends on this.

5. **OIDC redirect URIs** must include the path base. The callback path `/signin-oidc` is relative to path base.

### Build & Deploy

```powershell
az acr login --name h3econtainerregistry
dotnet publish src/Aspire.Dashboard/Aspire.Dashboard.csproj -c Release -r linux-x64 /p:PublishProfile=DefaultContainer /p:ContainerImageTag=<version> /p:ContainerRegistry=h3econtainerregistry.azurecr.io /p:ContainerRepository=aspire-dashboard
```

### Key Config

- `Dashboard__PathBase=/sandbox/us`
- `ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED=true`

### Reference

Full deployment documentation: [docs/PROXY-PATHBASE-DEPLOYMENT.md](../docs/PROXY-PATHBASE-DEPLOYMENT.md)
