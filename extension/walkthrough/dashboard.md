# The Aspire Dashboard

The Aspire Dashboard gives you real-time visibility into your running application. It's also a lightweight [OpenTelemetry](https://opentelemetry.io/) viewer for any structured logs, metrics, or traces you have instrumented in your app.

### What you'll see

- **Resources** — all your services, containers, and executables with their current state
- **Endpoints** — live URLs for each service
- **Console Logs** — aggregated output from all services in one place
- **Structured Logs** — searchable, structured log entries with OpenTelemetry
- **Traces** — distributed traces showing request flow across services
- **Metrics** — performance counters and custom metrics

### Health monitoring

Each resource shows its health status at a glance:

- 🟢 **Running** — service is healthy
- 🟡 **Starting** — service is spinning up
- 🔴 **Failed** — something went wrong

The dashboard launches automatically when you run your app and is accessible from a local URL in your terminal output.
