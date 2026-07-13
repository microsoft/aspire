# Aspire Dashboard backend

This project is the separately runnable ASP.NET Core Native AOT backend for the React dashboard.
It is intentionally additive: `Aspire.Dashboard` remains the default Blazor dashboard and continues
to host the existing `/api/deck` transport.

The first migration slice implements only version discovery and the `configuration` capability.
In side-by-side mode, React reads configuration from this host and delegates resources, telemetry,
commands, interactions, authentication, and terminal traffic to the existing dashboard. A version
must not advertise a capability until its complete black-box behavior passes the 157-feature parity
inventory in `src/Aspire.Deck/ui/e2e/parity`.

## Run

Bind this development-only slice to a loopback address:

```bash
ASPNETCORE_URLS=http://127.0.0.1:18889 \
DashboardBackend__ApplicationName=Stress \
  dotnet run --project src/Aspire.Dashboard.Backend/Aspire.Dashboard.Backend.csproj
```

The host exposes:

- `GET /api/dashboard` for version and capability discovery.
- `GET /api/dashboard/v1/config` for the version 1 configuration capability.

All JSON uses camel-case names and an explicit `JsonSerializerContext`. New contract payloads must
be registered with source generation so Native AOT never depends on reflection serialization.
