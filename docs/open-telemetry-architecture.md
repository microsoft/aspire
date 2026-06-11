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

## Non-.NET apps

OTEL isn't limited to .NET projects. Apps and containers that include OTEL can be passed environment variables to configure exporting telemetry. For example, the dapr sidecar (written in golang) includes OTEL and standard OTEL environment variables can be used to enable telemetry.
