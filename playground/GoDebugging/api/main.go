//go:build playground

package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"
)

func main() {
	message := flag.String("message", "hello", "message returned by the API")
	flag.Parse()

	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		response := map[string]any{
			"message": *message,
			"args":    os.Args[1:],
			"time":    time.Now().Format(time.RFC3339),
		}

		w.Header().Set("Content-Type", "application/json")
		if err := json.NewEncoder(w).Encode(response); err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
		}
	})

	server := &http.Server{
		Addr:              listenAddress(),
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("api listening on %s", server.Addr)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("listen: %v", err)
		}
	}()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = server.Shutdown(shutdownCtx)
}

func listenAddress() string {
	if port := os.Getenv("PORT"); port != "" {
		return ":" + port
	}

	if urls := os.Getenv("ASPNETCORE_URLS"); urls != "" {
		first := strings.Split(urls, ";")[0]
		if index := strings.LastIndex(first, ":"); index != -1 {
			return first[index:]
		}
	}

	return ":8080"
}
