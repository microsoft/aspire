/**
 * OpenTelemetry instrumentation for the app.
 *
 * When the OTEL_EXPORTER_OTLP_ENDPOINT environment variable is set (automatically
 * configured by Aspire), this module initializes the OpenTelemetry Node.js SDK to
 * collect and export distributed traces, metrics, and logs to the Aspire dashboard.
 *
 * This file must be imported before any other modules to ensure all libraries
 * are automatically instrumented.
 *
 * @see https://opentelemetry.io/docs/languages/js/getting-started/nodejs/
 */
import { env } from 'node:process';
import { NodeSDK } from '@opentelemetry/sdk-node';
import { getNodeAutoInstrumentations } from '@opentelemetry/auto-instrumentations-node';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { OTLPMetricExporter } from '@opentelemetry/exporter-metrics-otlp-grpc';
import { OTLPLogExporter } from '@opentelemetry/exporter-logs-otlp-grpc';
import { BatchLogRecordProcessor } from '@opentelemetry/sdk-logs';
import { PeriodicExportingMetricReader } from '@opentelemetry/sdk-metrics';

if (env.OTEL_EXPORTER_OTLP_ENDPOINT) {
  const sdk = new NodeSDK({
    traceExporter: new OTLPTraceExporter(),
    metricReader: new PeriodicExportingMetricReader({
      exporter: new OTLPMetricExporter(),
    }),
    logRecordProcessor: new BatchLogRecordProcessor(
      new OTLPLogExporter(),
    ),
    instrumentations: [getNodeAutoInstrumentations()],
  });

  sdk.start().catch((error) => {
    console.error('Failed to start OpenTelemetry NodeSDK:', error);
  });

  process.on('SIGTERM', () => {
    sdk.shutdown().finally(() => process.exit(0));
  });
  process.on('SIGINT', () => {
    sdk.shutdown().finally(() => process.exit(0));
  });
}
