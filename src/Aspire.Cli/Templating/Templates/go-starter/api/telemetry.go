// Configures traces, metrics, and logs from Aspire's OTLP environment variables.
package main

import (
	"context"
	"fmt"
	"io"
	"log"
	"log/slog"
	"net/url"
	"os"
	"strings"

	"go.opentelemetry.io/contrib/bridges/otelslog"
	"go.opentelemetry.io/contrib/instrumentation/runtime"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetricgrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	otellog "go.opentelemetry.io/otel/log/global"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

// setupOTel returns a shutdown func that flushes pending telemetry.
func setupOTel(ctx context.Context, serviceName string) (func(context.Context) error, error) {
	if os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT") == "" {
		// Keep standalone runs working when no exporter is configured.
		return func(context.Context) error { return nil }, nil
	}

	// Preserve Aspire's service identity so telemetry stays associated with this resource.
	res := resource.Default()

	headers := otlpHeaders()

	traceOpts := []otlptracegrpc.Option{}
	if len(headers) > 0 {
		traceOpts = append(traceOpts, otlptracegrpc.WithHeaders(headers))
	}
	traceExp, err := otlptracegrpc.New(ctx, traceOpts...)
	if err != nil {
		return nil, fmt.Errorf("trace exporter: %w", err)
	}
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithBatcher(traceExp),
		sdktrace.WithResource(res),
	)
	otel.SetTracerProvider(tp)

	metricOpts := []otlpmetricgrpc.Option{}
	if len(headers) > 0 {
		metricOpts = append(metricOpts, otlpmetricgrpc.WithHeaders(headers))
	}
	metricExp, err := otlpmetricgrpc.New(ctx, metricOpts...)
	if err != nil {
		return nil, fmt.Errorf("metric exporter: %w", err)
	}
	mp := sdkmetric.NewMeterProvider(
		sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metricExp)),
		sdkmetric.WithResource(res),
	)
	otel.SetMeterProvider(mp)

	if err := runtime.Start(runtime.WithMeterProvider(mp)); err != nil {
		return nil, fmt.Errorf("runtime metrics: %w", err)
	}

	logOpts := []otlploggrpc.Option{}
	if len(headers) > 0 {
		logOpts = append(logOpts, otlploggrpc.WithHeaders(headers))
	}
	logExp, err := otlploggrpc.New(ctx, logOpts...)
	if err != nil {
		return nil, fmt.Errorf("log exporter: %w", err)
	}
	lp := sdklog.NewLoggerProvider(
		sdklog.WithProcessor(sdklog.NewBatchProcessor(logExp)),
		sdklog.WithResource(res),
	)
	otellog.SetLoggerProvider(lp)

	// Mirror standard logs to stderr and OTel for both resource and structured log views.
	otelLogHandler := otelslog.NewHandler(serviceName, otelslog.WithLoggerProvider(lp))
	slog.SetDefault(slog.New(otelLogHandler))
	log.SetOutput(io.MultiWriter(os.Stderr, slog.NewLogLogger(otelLogHandler, slog.LevelInfo).Writer()))

	return func(ctx context.Context) error {
		_ = tp.Shutdown(ctx)
		_ = mp.Shutdown(ctx)
		_ = lp.Shutdown(ctx)
		return nil
	}, nil
}

// otlpHeaders parses OTEL_EXPORTER_OTLP_HEADERS into a map per the OTel spec
// (comma-separated key=value pairs, values may be percent-encoded). Aspire
// injects this with the dashboard's x-otlp-api-key auth header.
func otlpHeaders() map[string]string {
	raw := os.Getenv("OTEL_EXPORTER_OTLP_HEADERS")
	if raw == "" {
		return nil
	}
	out := make(map[string]string)
	for _, pair := range strings.Split(raw, ",") {
		pair = strings.TrimSpace(pair)
		if pair == "" {
			continue
		}
		key, value, ok := strings.Cut(pair, "=")
		if !ok {
			continue
		}
		key = strings.TrimSpace(key)
		value = strings.TrimSpace(value)
		if decoded, err := url.QueryUnescape(value); err == nil {
			value = decoded
		}
		out[key] = value
	}
	return out
}
