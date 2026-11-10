// Sample Go API using Aspire's injected telemetry and listen settings.
package main

import (
	"context"
	"encoding/json"
	"errors"
	// {{#redis}}
	"fmt"
	// {{/redis}}
	"log"
	"math/rand/v2"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	// {{#redis}}
	"github.com/redis/go-redis/extra/redisotel/v9"
	"github.com/redis/go-redis/v9"
	// {{/redis}}
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

// {{#redis}}
const cacheKey = "weatherforecast"
const cacheTTL = 5 * time.Second

var cache *redis.Client

// {{/redis}}
func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	shutdownTelemetry, err := setupOTel(ctx, "{{projectNameLower}}-api")
	if err != nil {
		log.Fatalf("otel: %v", err)
	}
	defer func() { _ = shutdownTelemetry(context.Background()) }()

	// {{#redis}}
	if err := setupRedis(); err != nil {
		log.Fatalf("cache: %v", err)
	}
	if cache != nil {
		defer func() { _ = cache.Close() }()
	}
	// {{/redis}}
	mux := http.NewServeMux()
	mux.HandleFunc("/api/weatherforecast", weatherHandler())
	mux.HandleFunc("/health", healthHandler())
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
	host := os.Getenv("ASPIRE_GO_BIND_ADDRESS")
	if host == "" {
		host = "localhost"
	}

	if port := os.Getenv("PORT"); port != "" {
		return host + ":" + port
	}
	if urls := os.Getenv("ASPNETCORE_URLS"); urls != "" {
		// "http://localhost:5000;https://localhost:5001" → "localhost:5000"
		first := strings.Split(urls, ";")[0]
		if i := strings.LastIndex(first, ":"); i != -1 {
			return host + first[i:]
		}
	}
	return host + ":8080"
}

// {{#redis}}
func setupRedis() error {
	cacheURI := os.Getenv("CACHE_URI")
	if cacheURI == "" {
		log.Println("CACHE_URI not set; running without cache")
		return nil
	}
	opts, err := redis.ParseURL(cacheURI)
	if err != nil {
		return fmt.Errorf("parse CACHE_URI: %w", err)
	}

	cache = redis.NewClient(opts)
	if err := redisotel.InstrumentTracing(cache); err != nil {
		_ = cache.Close()
		return fmt.Errorf("instrument Redis tracing: %w", err)
	}
	if err := redisotel.InstrumentMetrics(cache); err != nil {
		_ = cache.Close()
		return fmt.Errorf("instrument Redis metrics: %w", err)
	}
	return nil
}

// {{/redis}}
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

func weatherHandler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// {{#redis}}
		ctx := r.Context()

		if cache != nil {
			if cached, err := cache.Get(ctx, cacheKey).Result(); err == nil {
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(cached))
				return
			}
		}
		// {{/redis}}

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

		// {{#redis}}
		if cache != nil {
			_ = cache.Set(ctx, cacheKey, body, cacheTTL).Err()
		}
		// {{/redis}}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(body)
	}
}

func healthHandler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// {{#redis}}
		if cache != nil {
			if err := cache.Ping(r.Context()).Err(); err != nil {
				http.Error(w, fmt.Sprintf("cache unhealthy: %v", err), http.StatusServiceUnavailable)
				return
			}
		}
		// {{/redis}}
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("Healthy"))
	}
}
