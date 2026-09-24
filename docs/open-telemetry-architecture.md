# Aspire OpenTelemetry architecture

One of Aspire's objectives is to ensure that apps are straightforward to debug and diagnose. By default, Aspire apps are configured to collect and export telemetry using [OpenTelemetry (OTEL)](https://opentelemetry.io/). Additionally, Aspire local development includes UI in the dashboard for viewing OTEL data. Telemetry just works and is easy to use.

This document details how OpenTelemetry is used in Aspire apps.

## Telemetry types

OTEL is focused on three kinds of telemetry: structured logging, tracing, and metrics. .NET libraries and apps have APIs for recording each kind of telemetry:

* Structured logging: Log entries from `ILogger`.
* Tracing: Distributed tracing from `Activity`.
* Metrics: Numeric values from `Meter` and `Instrument<T>`.

When an OpenTelemetry SDK is configured in an app, it receives data from these APIs.

## OpenTelemetry SDK

The [.NET OpenTelemetry SDK](https://github.com/open-telemetry/opentelemetry-dotnet) offers features for gathering data from several .NET APIs, including `ILogger`, `Activity`, `Meter`, and `Instrument<T>`. It then facilitates the export of this telemetry data to a data store or reporting tool. The telemetry export mechanism relies on the [OpenTelemetry protocol (OTLP)](https://opentelemetry.io/docs/specs/otel/protocol/), which serves as a standardized approach for transmitting telemetry data through REST or gRPC.

.NET projects setup the .NET OpenTelemetry SDK using the _service defaults_ project. Aspire templates automatically create the service defaults, and Aspire apps call it at startup. The service defaults enable collecting and exporting telemetry for .NET apps.

## OpenTelemetry environment variables

OTEL has a [list of known environment variables](https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/) that configure the most important behavior for collecting and exporting telemetry. OTEL SDKs, including the .NET SDK, support reading these variables.

Aspire apps launch with environment variables that configure the name and ID of the app in exported telemetry and set the address endpoint of the OTLP server to export data. For example:

* `OTEL_SERVICE_NAME` = myfrontend
* `OTEL_RESOURCE_ATTRIBUTES` = service.instance.id=1a5f9c1e-e5ba-451b-95ee-ced1ee89c168
* `OTEL_EXPORTER_OTLP_ENDPOINT` = http://localhost:4318

The environment variables are automatically set in local development.

## Aspire local development

The Aspire dashboard provides UI for viewing the telemetry of apps. Telemetry data is sent to the dashboard using OTLP, and the dashboard implements an OTLP server to receive telemetry data and store it in memory. The dashboard UI presents telemetry stored in memory.

Aspire debugging workflow:

* Developer starts the Aspire app with debugging, presses <kbd>F5</kbd>.
* Aspire dashboard and developer control plane (DCP) start.
* App configuration is run in the _AppHost_ project.
  * OTEL environment variables are automatically added to .NET projects during app configuration.
  * DCP provides the name (`OTEL_SERVICE_NAME`) and ID (`OTEL_RESOURCE_ATTRIBUTES`) of the app in exported telemetry.
  * The OTLP endpoint is an HTTP/2 port started by the dashboard. This endpoint is set in the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable on each project. That tells projects to export telemetry back to the dashboard.
  * Small export intervals (`OTEL_BSP_SCHEDULE_DELAY`, `OTEL_BLRP_SCHEDULE_DELAY`, `OTEL_METRIC_EXPORT_INTERVAL`) so data is quickly available in the dashboard. Small values are used in local development to prioritize dashboard responsiveness over efficiency.
* The DCP starts configured projects, containers, and executables.
* Once started, apps send telemetry to the dashboard.
* Dashboard displays near real-time telemetry of all Aspire apps.

## Aspire deployment

Aspire deployment environments should configure OTEL environment variables that make sense for their environment. For example, `OTEL_EXPORTER_OTLP_ENDPOINT` should be configured to the environment's local OTLP collector or monitoring service.

Aspire telemetry works best in environments that support OTLP. OTLP exporting is disabled if `OTEL_EXPORTER_OTLP_ENDPOINT` isn't configured.

### OpenTelemetry upgrade limits

OpenTelemetry 1.18 reduces the default maximum serialized OTLP request from 128 MiB to 64 MiB. A batch exceeding that limit is dropped. Applications that need the previous capacity can configure `OtlpExporterOptions.MaxRequestSizeBytes` to `128 * 1024 * 1024`. The default maximum OTLP response is now 4 MiB; oversized responses are treated as non-retryable failures.

Newly generated ServiceDefaults projects use the updated package versions. Existing generated projects retain their package references until explicitly updated.

## Non-.NET apps

OTEL isn't limited to .NET projects. Apps and containers that include OTEL can be passed environment variables to configure exporting telemetry. For example, the dapr sidecar (written in golang) includes OTEL and standard OTEL environment variables can be used to enable telemetry.

## Agent usage telemetry

Agent usage reporting is separate from application OTLP telemetry. `aspire agent init` registers an all-tool hook for supported Copilot and Claude clients. Rerun initialization after updating the CLI to replace existing script registrations with direct executable registrations; unrelated user hooks are preserved.

The native hook receives every invocation but only reports the existing allowlisted Aspire skill, reference-file, and MCP-tool events. It runs through normal CLI command dispatch without launching PowerShell or Bash. The agent command opts out of automatic telemetry initialization, so unrelated invocations return without constructing exporters or starting enrichment. Neither wildcard coverage nor event sampling is reduced.

The native classifier reads skill names and `references/` file inventories from the embedded bundle's `skill-manifest.json`, without extracting files to disk. Only manifest-listed reference paths are eligible; `SKILL.md` reads are skill invocations, and other assets such as evals and scripts are not reference events. MCP tool names still come from the embedded canonical hook because the manifest does not yet contain a tool inventory. Neither source is read from mutable installed scripts or arbitrary local skills. Tests verify that the shipped manifest preserves the canonical hook's skill/reference reporting set.

`ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS` controls the native hook's input memory bound before JSON parsing. Its default is 65,536 UTF-16 characters, matching the legacy PowerShell hook; valid values range from 1 to 1,048,576. Larger input is drained without being retained or reported. Invalid configuration is reported on stderr without interrupting the agent. The hook reads this setting through `IEnvironment` before initializing telemetry.

Eligible events pass through the existing `aspire agent telemetry` command and are persisted to Azure Monitor Exporter's disk-backed storage before the hook returns. Uploading is not on the hook's critical path. An independent CLI uploader keeps the exporter alive while there is pending data, using the exporter's own batching, retry, and cross-process lease recovery. No additional queue format or ingestion client is used.

The uploader survives the originating agent process and exits when storage is drained. A failed launch leaves persisted data for a later invocation to recover. An interrupted upload may remain leased for several minutes before retry; delivery is at-least-once, so a crash after acceptance can result in duplicates. Disk access failures, exporter storage limits, permanent ingestion errors, and retention still limit delivery. Persistence/launch failures are recorded in CLI logs without breaking the agent.

`ASPIRE_CLI_TELEMETRY_OPTOUT` continues to suppress collection and uploader startup for opted-out invocations. Like other environment settings, it is inherited by processes at launch; changing a shell environment does not retroactively alter an already-running process.
