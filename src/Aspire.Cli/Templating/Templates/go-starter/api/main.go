// Sample Go API for the Aspire starter template. Aspire injects CACHE_URI for
// optional Redis, OTEL_EXPORTER_OTLP_* for telemetry, and the listen endpoint.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math/rand/v2"
	"net/http"
	"os"
	"os/signal"
	"runtime"
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

	cache, err := newRedisClient()
	if err != nil {
		log.Fatalf("cache: %v", err)
	}
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
	mux.Handle("/", http.FileServer(http.Dir("./static")))

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

// listenAddress uses Aspire's endpoint, or loopback:8080 for standalone runs.
func listenAddress() string {
	host := "0.0.0.0"
	if runtime.GOOS == "windows" {
		// Each `go run` build has a new executable path, so loopback avoids repeated Windows Firewall prompts.
		host = "127.0.0.1"
	}

	if port := os.Getenv("PORT"); port != "" {
		return host + ":" + port
	}
	if urls := os.Getenv("ASPNETCORE_URLS"); urls != "" {
		// "http://localhost:5000;https://localhost:5001" → "127.0.0.1:5000"
		first := strings.Split(urls, ";")[0]
		if i := strings.LastIndex(first, ":"); i != -1 {
			return host + first[i:]
		}
	}
	return host + ":8080"
}

// newRedisClient returns nil if no Redis URI is provided.
func newRedisClient() (*redis.Client, error) {
	cacheURI := os.Getenv("CACHE_URI")
	if cacheURI == "" {
		log.Println("CACHE_URI not set; running without cache")
		return nil, nil
	}
	opts, err := redis.ParseURL(cacheURI)
	if err != nil {
		return nil, fmt.Errorf("parse CACHE_URI: %w", err)
	}
	return redis.NewClient(opts), nil
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
