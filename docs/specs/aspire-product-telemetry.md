# Aspire product telemetry

## Scope

Aspire applications report product usage and errors to help understand how Aspire is used. This specification describes the reporting implemented by the Aspire CLI and Dashboard, including agent usage reported through the CLI.

Product telemetry is separate from the operational telemetry of user applications. Application logs, traces, and metrics received by the Dashboard are not forwarded to Microsoft's product telemetry destination. Ordinary CLI, Dashboard, and framework logs are not exported through the product pipeline either. Application observability is described in [Aspire OpenTelemetry architecture](../open-telemetry-architecture.md).

The CLI and Dashboard send product telemetry directly to Azure Monitor using separate, built-in Application Insights connection strings. This reporting does not require an OpenTelemetry Collector or an IDE debug session.

## Shared reporting pipeline

Each application has a recording service and a telemetry manager:

| Responsibility | Implementation |
| --- | --- |
| Shared activity creation, structured event recording, and property filtering | [AspireTelemetryBase](../../src/Shared/Telemetry/AspireTelemetryBase.cs) |
| CLI instrumentation and resolved machine, version, environment, and coding-agent metadata | [AspireCliTelemetry](../../src/Aspire.Cli/Telemetry/AspireCliTelemetry.cs) |
| Dashboard instrumentation and classified-property policy | [DashboardTelemetryService](../../src/Aspire.Dashboard/Telemetry/DashboardTelemetryService.cs) |
| CLI enablement, initialization, and shutdown | [TelemetryManager](../../src/Aspire.Cli/Telemetry/TelemetryManager.cs) |
| Dashboard hosted initialization and shutdown | [DashboardTelemetryManager](../../src/Aspire.Dashboard/Telemetry/DashboardTelemetryManager.cs) |
| Azure Monitor trace/log provider configuration and ownership | [AzureMonitorTelemetryProvider](../../src/Shared/Telemetry/AzureMonitorTelemetryProvider.cs) |

The recording services derive from `AspireTelemetryBase`. They supply activity source names, an error event name, default metadata, and a property policy. The base class records telemetry; it does not construct or own exporters.

After checking enablement, a manager supplies a dedicated `ServiceCollection` to `AzureMonitorTelemetryProvider`. The shared owner creates the reported tracer provider and a private logger factory containing the Azure Monitor product log provider. Application logger providers, filters, and scopes are not imported into that factory.

Only the product's reported activity source is listened to by its Azure Monitor tracer provider. Usage and error logs are written at Information level through the private event logger in the product event category. Ordinary application and framework logs are excluded by logger-factory isolation, not by product-specific category or level filters on the OpenTelemetry logger provider or batch processor. Formatted messages are included; scopes are excluded.

| Application | Reported activity source | Product event log category |
| --- | --- | --- |
| CLI | `Aspire.Cli.Reported` | `Aspire.Cli.Reported.Events` |
| Dashboard | `Aspire.Dashboard.Reported` | `Aspire.Dashboard.Reported.Events` |

The manager attaches the private event logger through `SetEventLogger` after successful provider creation and detaches it before shutdown. The recording service otherwise uses `NullLogger.Instance`. Events recorded without an attached product logger are not buffered for later replay.

The CLI's ordinary startup logger factory also registers an OpenTelemetry logger provider, with formatted messages and scopes enabled. That registration does not configure a log exporter and is independent of the isolated product pipeline. Local file and console logging remain available after product telemetry shuts down.

## Enablement

Product reporting is enabled by default, subject to application configuration and command selection:

| Setting or invocation | Behavior |
| --- | --- |
| `ASPIRE_CLI_TELEMETRY_OPTOUT=true` or `1` | Disables CLI product reporting, including agent reporting and uploader startup. |
| `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT=true` or `1` | Disables Dashboard product reporting. |
| `Dashboard:DebugSession:TelemetryOptOut=true`, also exposed as `DASHBOARD__DEBUGSESSION__TELEMETRYOPTOUT` | AppHost-forwarded Dashboard opt-out; either Dashboard opt-out setting disables reporting. |
| CLI help/version informational invocations and completion | Do not enable the reported pipeline. |
| Agent hook with no eligible event | Does not initialize product providers or background metadata enrichment. |

[TelemetryConfiguration](../../src/Aspire.Cli/Telemetry/TelemetryConfiguration.cs) resolves CLI provider selection. Ordinary commands initialize their providers before metadata enrichment. Commands that defer telemetry startup, including the agent telemetry command, initialize only when they have work to report.

[DashboardTelemetryConfiguration](../../src/Aspire.Dashboard/Telemetry/DashboardTelemetryConfiguration.cs) resolves Dashboard reporting enablement. The hosted manager initializes its providers at startup. If exporter or storage initialization fails, the manager logs a local warning, releases partially created providers, and allows Dashboard startup to continue without product export. The recording service's enablement represents configuration, not successful initialization or delivery.

Product opt-out does not disable separately configured diagnostic or profiling OTLP export.

## Logs and spans

### Usage and error logs

`RecordEvent` writes an Information-level structured log immediately. The event name is the message and `EventId.Name`; the recorder also sets `microsoft.operation_name` so Azure Monitor uses the event name for `operation_Name` / `OperationName`.

`RecordError` uses the same structured-log path with application-specific exception properties. The product log carries no exception object. Azure Monitor stores these records in `traces` / `AppTraces`.

Events and errors do not create activities or add activity events. Logs use only the trace/span correlation supplied by an ambient activity. When there is no activity, the recorder does not generate correlation IDs. Recording an error does not change an ambient activity's status.

Default metadata and event properties pass through the application's property policy before logging. String collections admitted by that policy are serialized with `string.Join(",")`; individual values are not escaped. The Dashboard policy excludes collection values. Activity tags retain their typed values.

### Operations

Reported operations use activities whose duration ends when the caller disposes them. The CLI records command and operation spans, including `aspire/cli/main` for ordinary invocations. CLI spans are enriched with currently resolved tags by `CliTagEnrichmentProcessor.OnEnd`, before the Azure export processor queues them.

The Dashboard uses the following events and operation:

| Name | Representation |
| --- | --- |
| `aspire/dashboard/component/initialize` | Structured log |
| `aspire/dashboard/component/paramsSet` | Structured log |
| `aspire/dashboard/component/dispose` | Structured log |
| `aspire/dashboard/error` | Structured log |
| `aspire/dashboard/command` | Internal activity, exported as an Application Insights dependency |

Component lifecycle events are recorded when they occur, with any available ambient trace correlation. [DashboardCommandExecutor](../../src/Aspire.Dashboard/Model/DashboardCommandExecutor.cs) calls `StartOperation`, sets the result through `SetOperationStatus`, and disposes the activity after command execution, before the UI recovery delay.

Dashboard recording APIs accept classified properties. Activity property setters also apply the shared property policy, but direct mutation of a returned `Activity` bypasses that policy. Instrumentation must use the recording service's property APIs.

## Data policies

### Dashboard

The Dashboard admits known property keys and excludes PII-classified values, resource names, raw browser user agents, exception messages, and stack traces. It reports bounded component, command, and UI metadata, plus Dashboard version/build information and exception type/runtime version.

The property policy enforces:

- Scalar strings: at most 1,024 characters.
- Other supported scalar values: booleans, integers, and doubles.
- Classified numeric properties: values must parse to finite doubles.
- Collections, other unsupported value types, and unknown property keys: excluded.

[TelemetryErrorRecorder](../../src/Aspire.Dashboard/Telemetry/TelemetryErrorRecorder.cs) handles explicitly recorded errors and the unhandled Blazor circuit errors observed by [TelemetryLoggerProvider](../../src/Aspire.Dashboard/Telemetry/TelemetryLoggerProvider.cs). Aggregate exceptions are flattened and distinct leaves are reported once per call, using type, message, and stack trace for deduplication. Messages and stacks are not exported. Empty aggregates still produce an error log; separate recording calls remain separate occurrences. Optional local logging writes the original exception once.

### CLI

The CLI retains properties supplied by its internal instrumentation and does not apply the Dashboard's allowlist. Its error properties include exception type, message, and stack trace. Resolved default metadata includes machine, CLI identity, environment, and coding-agent information. CLI log events use the metadata available when recorded; spans receive resolved metadata at the end of the activity.

Agent input has an additional classification and validation boundary described below. That boundary limits agent-supplied fields; it does not replace the CLI's shared enrichment policy.

## Resource identity and export

The resource `service.name` is `aspire-cli` for the CLI and `aspire-dashboard` for Dashboard product telemetry. Both applications set `service.version` to the physical binary's full informational assembly version using `AssemblyVersionHelper.GetInformationalVersion`, including build metadata when present. Emulated CLI identity is reported separately in identity tags, not substituted for the resource version.

The CLI and Dashboard construct their product resources explicitly from the service name, binary version, and a generated service instance ID, without environment-variable detectors. Product trace and log providers share that explicit resource. The CLI's separate profiling and diagnostic trace providers retain default SDK resource detection, including environment-derived attributes. The Dashboard's separate diagnostic OTLP resource defaults to `aspire-dashboard` and supports configured environment-derived attributes.

Azure Monitor derives cloud-role and application-version fields from the resource. The Application Insights destination is selected by the connection string, not the logical service name. The workspace [`_ResourceId`](https://learn.microsoft.com/azure/azure-monitor/reference/tables/apptraces) identifies that destination Azure resource; it is not generated from `service.name`.

[AspireTelemetryExporter](../../src/Shared/Telemetry/AspireTelemetryExporter.cs) configures both products' Azure exporters. Live Metrics, standard duration metrics, and performance counters are disabled. The trace exporter uses `SamplingRatio = 1.0` with `TracesPerSecond` cleared. Structured event logs do not depend on a sampled reported activity. These options do not configure application or diagnostic OTLP telemetry; other exporter metadata behavior follows the Azure SDK's defaults.

These options do not disable the Azure SDK's separate Statsbeat pipeline, which can collect hosting identifiers and uses its own default resource independently of the product resource. Disabling it from code without process-wide environment changes is tracked by [Azure/azure-sdk-for-net#63651](https://github.com/Azure/azure-sdk-for-net/issues/63651).

CLI profiling requires an explicit profiling setting and an OTLP endpoint. `ASPIRE_PROFILING_ENABLED=true` enables profiling; `ASPIRE_STARTUP_PROFILING_ENABLED` is also accepted when the primary setting is absent. In DEBUG, an OTLP endpoint without profiling selects diagnostic export instead. `ASPIRE_CLI_CONSOLE_EXPORTER_LEVEL=Diagnostic` enables the separate diagnostic console exporter; `Reported` attaches a console exporter to the Azure Monitor tracer provider with the same resource.

## Persistence and lifecycle

Exporter storage is rooted in the current user's profile, not `ASPIRE_HOME`:

| Application | Shared trace/log storage root |
| --- | --- |
| CLI | `.aspire/cli/telemetrystorage` |
| Dashboard | `.aspire/dashboard/telemetrystorage` |

Azure Monitor exporter 1.9 caches its transmitter by connection string. Each product uses the same connection string for its trace and log exporters, so both signals share a transmitter and storage. `AzureMonitorTelemetryProvider` configures both exporters with the same per-product storage directory.

The Azure exporter owns batching, disk storage, retries, retention, and cross-process leases. Buffering and storage are best effort: abrupt termination, storage limits, filesystem failures, and ingestion errors can prevent delivery.

`AzureMonitorTelemetryProvider` owns the tracer provider, log provider, and private logging services. Force flush and shutdown run concurrently for traces and logs and combine their success results. Shutdown is idempotent and waits for in-flight force flushes before shutting down either provider. Disposal releases the product providers without disposing the application's logger factory.

| Operation | SDK timeout per provider |
| --- | --- |
| Dashboard shutdown | 5,000 ms |
| CLI reported force flush | 3,000 ms |
| CLI normal shutdown, Release | 200 ms |
| CLI normal shutdown, DEBUG | No timeout |

These are SDK operation timeouts, not guarantees of total wall-clock shutdown time or successful ingestion. Pending force flushes and disposal can add time. Managers log a local warning when reported-provider shutdown times out.

## Agent usage telemetry

### Registration and classification

`aspire agent init` registers all-tool post-invocation hooks for supported Copilot and Claude clients through [TelemetryHookConfigurator](../../src/Aspire.Cli/Agents/Hooks/TelemetryHookConfigurator.cs). Hooks invoke `aspire agent telemetry --hook` directly through the CLI executable or its managed entry point, without launching PowerShell or Bash. Re-running initialization refreshes Aspire's registration while preserving unrelated user hooks.

[AgentTelemetryHook](../../src/Aspire.Cli/Agents/Hooks/AgentTelemetryHook.cs) classifies incoming tool invocations before initializing telemetry. It reports only the Aspire-owned catalog:

| Event type | Eligible action |
| --- | --- |
| `skill_invocation` | An allowlisted Aspire skill invocation or a read of its `SKILL.md`. |
| `tool_invocation` | An allowlisted Aspire MCP tool invocation. |
| `reference_file_read` | A read of a manifest-listed reference under an Aspire skill's `references/` directory. |

[AgentTelemetryCatalog](../../src/Aspire.Cli/Agents/Hooks/AgentTelemetryCatalog.cs) obtains skill and reference inventories from the embedded bundle's `skill-manifest.json`, without extracting files. The MCP tool inventory comes from the embedded canonical hook. Mutable installed scripts, arbitrary local skills, and non-reference assets do not extend the reporting catalog.

The classifier produces event type, client name, timestamp, and the applicable skill, tool, or skills-relative reference identifier. It includes a session ID only when the incoming value is a GUID. It does not export the raw hook payload, prompts, tool arguments, file contents, or absolute paths.

### Input bounds and recording

`ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS` bounds input before JSON parsing. The default is 65,536 UTF-16 characters; valid values are 1 through 1,048,576. Oversized input is drained without being retained or reported. Invalid configuration is written to stderr without interrupting the agent.

[AgentTelemetryCommand](../../src/Aspire.Cli/Commands/AgentTelemetryCommand.cs) validates generated or directly supplied command fields again. Identifiers have bounded lengths and a conservative ASCII character set; reference paths must be skills-relative and reject absolute paths, backslashes, and traversal. When no valid properties survive, it emits nothing.

An eligible invocation initializes providers before metadata enrichment and records `aspire/cli/agent_telemetry` as a reported span. Agent telemetry invocations suppress the generic `aspire/cli/main` span and the first-run notice, and disable the reported internal-Microsoft detector diagnostic. This avoids counting a background hook as an ordinary CLI invocation.

The hook emits its continuation response and the command returns success even when telemetry processing fails. Caught processing failures are reported through local diagnostics without including the raw payload.

### Durable delivery

Agent spans use the CLI's normal Azure Monitor exporter and storage. Process-level exporter switches enable trace persistence on force flush and avoid a network drain on shutdown for agent telemetry invocations. The command requests a reported force flush before returning and logs a warning if it does not complete. Uploading is not on the hook's critical path.

[AgentTelemetryUploader](../../src/Aspire.Cli/Telemetry/AgentTelemetryUploader.cs) starts an independent CLI process using `aspire agent telemetry --drain` when storage contains pending data. The uploader keeps the exporters alive while their SDK drains storage, holds a cross-process lock to avoid competing uploaders, and exits when the backlog is drained. It does not introduce another queue format or ingestion client.

A failed uploader launch leaves persisted data for a later invocation to recover. Interrupted uploads can remain leased for several minutes. There is no exactly-once delivery guarantee: a crash after server acceptance can result in duplicates, and storage or ingestion failures can still lose data.

The CLI opt-out suppresses collection and uploader startup. Settings are inherited when a process launches; changing a shell's environment does not change the settings of an already-running uploader.
