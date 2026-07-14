# Aspire Dashboard backend

This project is the separately runnable ASP.NET Core Native AOT backend for the React dashboard.
It is intentionally additive: `Aspire.Dashboard` remains the default Blazor dashboard and continues
to host the existing `/api/deck` transport.

The backend currently implements version discovery plus the `configuration`, read-only `resources`
snapshot, SignalR `resources-live`, resource `commands`, resource-scoped console backlog/live, and read-only structured-log backlog/live
capabilities. In side-by-side mode, React reads those capabilities from this host and delegates
traces, metrics, destructive console/telemetry operations, interactions, authentication,
and terminal traffic to the existing dashboard. A version must not advertise a
capability until its
complete black-box behavior passes the 157-feature parity inventory in
`src/Aspire.Deck/ui/e2e/parity`.

The host targets .NET 10 because SignalR server trimming and Native AOT support begins in .NET 9.
This project remains separately selectable and does not change the target framework or runtime of
the existing Blazor dashboard.

## Run

Bind this development-only slice to a loopback address:

```bash
ASPNETCORE_URLS=http://127.0.0.1:18889 \
DashboardBackend__ApplicationName=Stress \
DashboardBackend__LegacyDashboardUrl=https://localhost:18888 \
ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=https://localhost:22000 \
DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE=ApiKey \
DASHBOARD__RESOURCESERVICECLIENT__APIKEY=<apphost-resource-service-key> \
  dotnet run --project src/Aspire.Dashboard.Backend/Aspire.Dashboard.Backend.csproj
```

Use the same resource-service endpoint, authentication mode, and API key supplied to the existing
dashboard process. `Unsecured` authentication requires no API-key setting.

The host enforces loopback connections and loopback browser origins because this first migration
slice does not yet own dashboard authentication. If the resource service cannot provide its first
snapshot within 10 seconds, resource requests return `503 Service Unavailable` while the host keeps
retrying. Set `DashboardBackend__InitialSnapshotTimeout` to a positive `TimeSpan` value to change
that startup timeout.

The host exposes:

- `GET /api/dashboard` for version and capability discovery.
- `GET /api/dashboard/v1/config` for the version 1 configuration capability.
- `GET /api/dashboard/v1/resources` for the current AppHost resource snapshot.
- `/api/dashboard/v1/resources/live` for the SignalR `WatchResources` server stream. Each
  subscription receives an authoritative snapshot followed by incremental upserts and deletes.
- `POST /api/dashboard/v1/commands/execute` to execute a command from the current resource snapshot.
- `GET /api/dashboard/v1/structured-logs` for a read-only OTLP structured-log backlog proxied from
  the existing loopback dashboard.
- `/api/dashboard/v1/structured-logs/live` for the SignalR `WatchStructuredLogs` server stream.
- `/api/dashboard/v1/console-logs/live` for the SignalR `WatchConsoleLogs(resourceName)` server
  stream. The existing dashboard supplies the resource backlog before live stdout/stderr batches.

`DashboardBackend__LegacyDashboardUrl` must identify the existing dashboard's loopback base URL.
The proxy forwards the incoming dashboard cookie or authorization header so the legacy dashboard
continues to own authentication and OTLP storage during this migration slice.

All HTTP and SignalR JSON uses camel-case names and an explicit `JsonSerializerContext`. New
contract payloads must be registered with source generation so Native AOT never depends on
reflection serialization.
