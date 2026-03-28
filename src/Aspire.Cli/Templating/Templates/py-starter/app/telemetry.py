import logging
import opentelemetry._logs as otel_logs
import opentelemetry.exporter.otlp.proto.grpc._log_exporter as log_exporter
import opentelemetry.exporter.otlp.proto.grpc.metric_exporter as metric_exporter
import opentelemetry.exporter.otlp.proto.grpc.trace_exporter as trace_exporter
import opentelemetry.metrics as otel_metrics
import opentelemetry.sdk._logs as otel_sdk_logs
import opentelemetry.sdk._logs.export as otel_logs_export
import opentelemetry.sdk.metrics as otel_sdk_metrics
import opentelemetry.sdk.metrics.export as otel_metrics_export
import opentelemetry.sdk.trace as otel_sdk_trace
import opentelemetry.sdk.trace.export as otel_trace_export
import opentelemetry.trace as otel_trace

def configure_opentelemetry():

    otel_trace.set_tracer_provider(otel_sdk_trace.TracerProvider())
    otlp_span_exporter = trace_exporter.OTLPSpanExporter()
    span_processor = otel_trace_export.BatchSpanProcessor(otlp_span_exporter)
    otel_trace.get_tracer_provider().add_span_processor(span_processor)

    otlp_metric_exporter = metric_exporter.OTLPMetricExporter()
    metric_reader = otel_metrics_export.PeriodicExportingMetricReader(otlp_metric_exporter, export_interval_millis=5000)
    otel_metrics.set_meter_provider(otel_sdk_metrics.MeterProvider(metric_readers=[metric_reader]))

    otel_logs.set_logger_provider(otel_sdk_logs.LoggerProvider())
    otlp_log_exporter = log_exporter.OTLPLogExporter()
    log_processor = otel_logs_export.BatchLogRecordProcessor(otlp_log_exporter)
    otel_logs.get_logger_provider().add_log_record_processor(log_processor)

    logging.basicConfig(
        level=logging.INFO,
        handlers=[logging.StreamHandler(), otel_sdk_logs.LoggingHandler(logger_provider=otel_logs.get_logger_provider())]
    )
