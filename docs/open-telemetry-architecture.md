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

## Aspire product telemetry

Product usage telemetry is separate from application OTLP telemetry. The CLI and dashboard derive from the shared `AspireTelemetryBase`, supplying their own activity source names, error event names, metadata, and configuration. Both use the shared Azure Monitor exporter configuration, with Live Metrics, standard duration metrics, and performance counters disabled and reported events fully sampled. The exporter's separate `_OTELRESOURCE_` metadata metric remains enabled by default; neither product overrides its process-level configuration. Application and diagnostic OTLP metrics are unaffected. Each app supplies its own Application Insights connection string, with separate destinations for the CLI and dashboard.

The shared `AspireTelemetryExporter.GetTelemetryStoragePath` resolves separate product storage directories under the user's profile: `.aspire/cli/telemetrystorage` and `.aspire/dashboard/telemetrystorage`. Each product configures its log exporter with a `logs` subdirectory of its telemetry storage path. These paths do not follow `ASPIRE_HOME`.

`AzureMonitorTelemetryProvider` configures and owns both products' Azure Monitor trace and log providers, using their reported activity source, event category, shared resource, destination, and persistence path. Product-specific trace configuration runs before the Azure export processor is registered, preserving the CLI's end-of-span enrichment before spans are queued. The CLI's optional reported console exporter is configured through the same callback. The shared owner flushes and shuts down both signals concurrently, combines their success results, and disposes the tracer provider and isolated logging services. Managers retain consent checks and own one instance of this helper; profiling and diagnostic trace providers remain separate and use their existing configuration.

`DashboardTelemetryManager` gives only its isolated Azure Monitor trace and log providers a resource with `service.name` set to `ddc-cor-prd-usce-ai-aspiredashboard`. Azure Monitor derives the cloud role from that resource. The separate OTLP diagnostic provider retains its `aspire-dashboard` fallback and its configured environment-derived identity.

The CLI's `TelemetryManager` creates two separate resource builders: Azure Monitor trace and log providers use `service.name` set to `ddc-cor-prd-usce-ai-aspirecli`, while profiling and diagnostic OTLP providers retain `aspire-cli`. Both CLI resources preserve default SDK resource detection, service-instance configuration, and the physical binary version. The DEBUG-only reported console exporter is attached to the Azure Monitor trace provider and therefore shares its resource.

Only explicitly reported product activities and product-event logs are sent to Microsoft. Diagnostic activities, ordinary CLI/dashboard/framework logs, dashboard SQLite/ASP.NET Core tracing, and application logs, traces, and metrics received by the dashboard are not included in these providers.

The shared protected `RecordEventCore` writes an information-level structured log immediately without adding an event to any activity. The log includes `microsoft.operation_name` set to the event name so Azure Monitor populates `operation_Name` (`OperationName` in `AppTraces`), independently of any ambient activity's name. Public `RecordEvent` APIs are product-specific: the dashboard validates results and passes classified properties to the shared recorder, while the CLI supplies its existing CLI-specific tags. `AspireTelemetryBase` filters properties, including default metadata and exception fields, through each product's `TrySanitizeProperty` policy before writing logs or operation properties. String collections are then joined with commas for log attributes, without escaping individual values, avoiding the Azure exporter's `System.String[]` conversion while preserving typed activity tags. The dashboard implements its key allowlist, classification exclusions, and value bounds; the CLI explicitly retains its existing internally supplied tags and exception details. The base type does not expose a public raw-property event recorder. The shared recorder does not create a span. `RecordError` uses the same logging path with product-specific exception fields, independently of its optional raw local error log.

The CLI retains its machine/agent enrichment and its `ASPIRE_CLI_TELEMETRY_OPTOUT` setting. CLI instrumentation and its enrichment processor set product activity tags through the service's `SetActivityProperty` and `SetActivityProperties` APIs, so both use the CLI property policy. These setters change only the supplied properties, without attaching implicit metadata or starting background enrichment; null activities are ignored. Local profiling instrumentation remains separate. CLI events and errors use the dedicated `Aspire.Cli.Reported.Events` log category and are exported through Azure Monitor independently of an active reported activity, without adding activity events. Ordinary CLI logs and raw local exception logs are not exported. Each telemetry manager supplies an isolated logging service collection containing only the Azure Monitor product log pipeline. The shared `AzureMonitorTelemetryProvider` configures exact-category and information-or-higher filtering at both logger-provider and batch-processor boundaries, excludes scopes, and owns cleanup of those services and the product trace provider. `AspireTelemetryBase` only records events through the logger supplied by the manager; it does not construct or own logging providers. The log exporter is created only when command selection and consent enable reported telemetry; completion, help/version opt-outs, and agent hooks without eligible events do not create it. Reported trace and log flushing is concurrent and bounded. CLI startup retains its ordinary logger factory, and local file and console logging remain available after reported telemetry shutdown or disposal.

CLI startup also registers a general OpenTelemetry logger provider with formatted messages and scopes enabled. This registration does not configure a log exporter and remains independent of the isolated Azure Monitor product pipeline, which excludes scopes.

`AspireTelemetryBase` initializes its product-event logger to `NullLogger.Instance` internally. Constructors receive only the ordinary local logger; managers attach the isolated product logger through `SetEventLogger` after successful provider initialization and detach it before shutdown. Test setup uses the same attachment method.

The dashboard's `TelemetryErrorRecorder` flattens aggregate exceptions and records each distinct leaf once per call, deduplicating by type, message, and stack trace. Messages and stacks are used only for local deduplication, not exported. Empty aggregates still produce a sanitized error event, and repeated recording calls remain separate occurrences. Optional local logging writes the original exception once.

The dashboard reports directly without requiring an IDE debug session. `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT=true` (or `1`) disables reporting. The legacy AppHost-forwarded `Dashboard:DebugSession:TelemetryOptOut` setting (`DASHBOARD__DEBUGSESSION__TELEMETRYOPTOUT`) is also read directly from configuration for backwards compatibility; either opt-out disables reporting. The obsolete debug-session transport settings are ignored. IDE telemetry preferences no longer control dashboard product reporting. `DashboardTelemetryConfiguration` resolves enablement once, `DashboardTelemetryService` records instrumentation using those settings and the manager-supplied event logger, and the concrete hosted `DashboardTelemetryManager` owns provider initialization, shutdown, and isolated logging services. The recording service does not depend on the manager or a transport interface; its enablement represents configuration, not provider lifecycle. Provider initialization is attempted before the dashboard starts accepting requests. Exporter or storage initialization failures are logged locally, partially created providers and logging services are disposed, and the dashboard continues without product export. Successfully initialized providers are flushed concurrently with a bounded wait on shutdown.

Dashboard component initialization, parameter changes, and disposal all use a dashboard overload of `RecordEvent` to validate the result before the shared recorder applies the dashboard property policy. The isolated product logger factory uses a provider-specific category filter for product event logs, including sanitized errors. The Azure Monitor batch processor independently enforces the exact product-event category and information-or-higher level before queueing. Application logging configuration cannot suppress product events or enable export of ordinary dashboard/framework logs. The application logger factory remains unchanged and still receives those ordinary logs, but not product events. Log scopes are excluded. The log exporter is created only after hosted-service startup checks opt-out, and diagnostic OTLP resource configuration is kept separate from product metadata. Shutdown detaches the event logger, flushes the product log provider, and disposes the manager-owned logging services without disposing the application logger factory. Events create no spans and log correlation uses only the ambient activity's trace/span IDs, without component lifecycle links, generated event IDs, or activity events. Usage data is queued immediately rather than waiting for a component-lifetime span to complete, although abrupt termination can still lose buffered exporter data.

Timed resource commands use `StartOperation`, set their result with `SetOperationResult`, and dispose the returned nullable `Activity` at the end of command execution, before the UI recovery delay. Both property-writing paths use the shared activity property APIs. The dashboard's `SetActivityProperties` overload accepts classified dashboard properties and preserves their classification through shared filtering. There is no separate user-task event API or positional operation-context wrapper. The property policy covers the recording APIs, not direct mutation of returned `Activity` objects; callers must not attach unclassified data directly.

Blazor's unhandled circuit error logging hook and explicit handled-error call sites use the shared error recorder. Errors are recorded only as sanitized structured logs, without creating an activity or adding activity events. Correlation uses the ambient activity's trace/span IDs when present; no correlation IDs are generated when there is no activity. These logs carry no exception object, so they are recorded in Application Insights' traces table rather than the exceptions table.

The dashboard preserves the IDE bridge's exclusion of exception messages, stack traces, resource names, unknown property keys, and PII-classified fields. Raw browser user agents are also excluded. Default dashboard version/build properties are attached to activities and usage/error logs. The extension's legacy HTTP telemetry endpoints remain available for older dashboard versions.

## Agent usage telemetry

Agent usage reporting is separate from application OTLP telemetry. `aspire agent init` registers an all-tool hook for supported Copilot and Claude clients. Rerun initialization after updating the CLI to replace existing script registrations with direct executable registrations; unrelated user hooks are preserved.

The native hook receives every invocation but only reports the existing allowlisted Aspire skill, reference-file, and MCP-tool events. It runs through normal CLI command dispatch without launching PowerShell or Bash. `TelemetryManager` construction does not create providers: ordinary commands call `Initialize()` before enrichment, while the agent command defers initialization until classification finds an eligible event. For a hook invocation with no eligible event, `TryShutdownAsync()` skips shutdown without constructing exporters or starting enrichment. Neither wildcard coverage nor event sampling is reduced.

The native classifier reads skill names and `references/` file inventories from the embedded bundle's `skill-manifest.json`, without extracting files to disk. Only manifest-listed reference paths are eligible; `SKILL.md` reads are skill invocations, and other assets such as evals and scripts are not reference events. MCP tool names still come from the embedded canonical hook because the manifest does not yet contain a tool inventory. Neither source is read from mutable installed scripts or arbitrary local skills. Tests verify that the shipped manifest preserves the canonical hook's skill/reference reporting set.

`ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS` controls the native hook's input memory bound before JSON parsing. Its default is 65,536 UTF-16 characters, matching the legacy PowerShell hook; valid values range from 1 to 1,048,576. Larger input is drained without being retained or reported. Invalid configuration is reported on stderr without interrupting the agent. The hook reads this setting through `IEnvironment` before initializing telemetry.

Eligible events pass through the existing `aspire agent telemetry` command and are persisted to Azure Monitor Exporter's disk-backed storage before the hook returns. Uploading is not on the hook's critical path. An independent CLI uploader keeps the exporter alive while there is pending data, using the exporter's own batching, retry, and cross-process lease recovery. No additional queue format or ingestion client is used.

The uploader survives the originating agent process and exits when storage is drained. A failed launch leaves persisted data for a later invocation to recover. An interrupted upload may remain leased for several minutes before retry; delivery is at-least-once, so a crash after acceptance can result in duplicates. Disk access failures, exporter storage limits, permanent ingestion errors, and retention still limit delivery. Persistence/launch failures are recorded in CLI logs without breaking the agent.

`ASPIRE_CLI_TELEMETRY_OPTOUT` continues to suppress collection and uploader startup for opted-out invocations. Like other environment settings, it is inherited by processes at launch; changing a shell environment does not retroactively alter an already-running process.
