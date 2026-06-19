// Sample Go API for the Aspire starter template.
//
// Aspire injects:
//   - ConnectionStrings__cache  → Redis connection string (set via WithReference(cache))
//   - OTEL_EXPORTER_OTLP_*      → OTLP endpoint + headers (set via WithOtlpExporter)
//   - PORT / ASPNETCORE_URLS    → host endpoint Aspire wants this app to listen on
package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math/rand/v2"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/redis/go-redis/v9"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/metric"
)

const cacheKey = "weatherforecast"
const cacheTTL = 5 * time.Second

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	shutdownTelemetry, err := setupOTel(ctx, "{{projectNameLower}}-api")
	if err != nil {
		log.Fatalf("otel: %v", err)
	}
	defer func() { _ = shutdownTelemetry(context.Background()) }()

	cache := newRedisClient()
	if cache != nil {
		defer func() { _ = cache.Close() }()
	}

	meter := otel.Meter("aspired-api")
	cacheRequests, err := meter.Int64Counter(
		"aspired.api.cache.requests",
		metric.WithDescription("Weather forecast cache lookups by result"),
		metric.WithUnit("{request}"),
	)
	if err != nil {
		log.Fatalf("counter: %v", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/weatherforecast", weatherHandler(cache, cacheRequests))
	mux.HandleFunc("/health", healthHandler(cache))
	mux.HandleFunc("/", healthHandler(cache))

	addr := listenAddress()
	srv := &http.Server{
		Addr:              addr,
		Handler:           otelhttp.NewHandler(mux, "api"),
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("api listening on %s", addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("listen: %v", err)
		}
	}()

	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)
}

// listenAddress resolves the address Aspire wants this service to bind to.
// Falls back to :8080 for standalone runs.
func listenAddress() string {
	if port := os.Getenv("PORT"); port != "" {
		return ":" + port
	}
	if urls := os.Getenv("ASPNETCORE_URLS"); urls != "" {
		// "http://localhost:5000;https://localhost:5001" → ":5000"
		first := strings.Split(urls, ";")[0]
		if i := strings.LastIndex(first, ":"); i != -1 {
			return first[i:]
		}
	}
	return ":8080"
}

// newRedisClient returns nil if no Redis connection string is provided.
func newRedisClient() *redis.Client {
	connStr := os.Getenv("ConnectionStrings__cache")
	if connStr == "" {
		log.Println("ConnectionStrings__cache not set; running without cache")
		return nil
	}
	opts, err := redis.ParseURL(connStr)
	if err != nil {
		// Fall back to assuming "host:port[,password=…]" Aspire-style.
		opts = parseAspireRedisConnString(connStr)
	}
	return redis.NewClient(opts)
}

// parseAspireRedisConnString accepts the comma-separated key/value form Aspire
// emits for Redis (e.g. "localhost:6379,password=…,ssl=true").
func parseAspireRedisConnString(s string) *redis.Options {
	opts := &redis.Options{}
	for i, part := range strings.Split(s, ",") {
		part = strings.TrimSpace(part)
		if i == 0 {
			opts.Addr = part
			continue
		}
		k, v, ok := strings.Cut(part, "=")
		if !ok {
			continue
		}
		switch strings.TrimSpace(k) {
		case "password":
			opts.Password = strings.TrimSpace(v)
		case "ssl":
			if strings.EqualFold(strings.TrimSpace(v), "true") {
				opts.TLSConfig = &tls.Config{MinVersion: tls.VersionTLS12}
			}
		}
	}
	return opts
}

type forecast struct {
	Date         string `json:"date"`
	TemperatureC int    `json:"temperatureC"`
	TemperatureF int    `json:"temperatureF"`
	Summary      string `json:"summary"`
}

var summaries = []string{
	"Freezing", "Bracing", "Chilly", "Cool", "Mild",
	"Warm", "Balmy", "Hot", "Sweltering", "Scorching",
}

func weatherHandler(cache *redis.Client, cacheRequests metric.Int64Counter) http.HandlerFunc {
	hit := metric.WithAttributes(attribute.String("result", "hit"))
	miss := metric.WithAttributes(attribute.String("result", "miss"))
	skip := metric.WithAttributes(attribute.String("result", "skip"))

	return func(w http.ResponseWriter, r *http.Request) {
		ctx := r.Context()

		if cache != nil {
			if cached, err := cache.Get(ctx, cacheKey).Result(); err == nil {
				cacheRequests.Add(ctx, 1, hit)
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(cached))
				return
			}
			cacheRequests.Add(ctx, 1, miss)
		} else {
			cacheRequests.Add(ctx, 1, skip)
		}

		forecasts := make([]forecast, 5)
		for i := range forecasts {
			tempC := rand.IntN(75) - 20
			forecasts[i] = forecast{
				Date:         time.Now().AddDate(0, 0, i+1).Format("2006-01-02"),
				TemperatureC: tempC,
				TemperatureF: tempC*9/5 + 32,
				Summary:      summaries[rand.IntN(len(summaries))],
			}
		}

		body, err := json.Marshal(forecasts)
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}

		if cache != nil {
			_ = cache.Set(ctx, cacheKey, body, cacheTTL).Err()
		}

		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(body)
	}
}

func healthHandler(cache *redis.Client) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if cache != nil {
			if err := cache.Ping(r.Context()).Err(); err != nil {
				http.Error(w, fmt.Sprintf("cache unhealthy: %v", err), http.StatusServiceUnavailable)
				return
			}
		}
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("Healthy"))
	}
}
