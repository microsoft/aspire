# Aspire Dashboard backend

This project is the separately runnable ASP.NET Core Native AOT backend for the React dashboard.
It is intentionally additive: `Aspire.Dashboard` remains the default Blazor dashboard and continues
to host the existing `/api/deck` transport.

The backend currently implements version discovery plus the `configuration` and read-only
`resources` snapshot capabilities. In side-by-side mode, React reads those capabilities from this
host and delegates telemetry, commands, interactions, authentication, and terminal traffic to the
existing dashboard. A version must not advertise a capability until its complete black-box behavior
passes the 157-feature parity inventory in `src/Aspire.Deck/ui/e2e/parity`.

## Run

Bind this development-only slice to a loopback address:

```bash
ASPNETCORE_URLS=http://127.0.0.1:18889 \
DashboardBackend__ApplicationName=Stress \
ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=https://localhost:22000 \
DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE=ApiKey \
DASHBOARD__RESOURCESERVICECLIENT__APIKEY=<apphost-resource-service-key> \
  dotnet run --project src/Aspire.Dashboard.Backend/Aspire.Dashboard.Backend.csproj
```

Use the same resource-service endpoint, authentication mode, and API key supplied to the existing
dashboard process. `Unsecured` authentication requires no API-key setting.

The host exposes:

- `GET /api/dashboard` for version and capability discovery.
- `GET /api/dashboard/v1/config` for the version 1 configuration capability.
- `GET /api/dashboard/v1/resources` for the current AppHost resource snapshot.

All JSON uses camel-case names and an explicit `JsonSerializerContext`. New contract payloads must
be registered with source generation so Native AOT never depends on reflection serialization.
