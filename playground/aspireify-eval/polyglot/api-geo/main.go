package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
)

type GeoResult struct {
	City      string  `json:"city"`
	Lat       float64 `json:"lat"`
	Lng       float64 `json:"lng"`
	Source    string  `json:"source"`
	APIKeySet bool    `json:"api_key_set"`
}

func main() {
	port := os.Getenv("PORT")
	if port == "" {
		port = "8002"
	}

	apiKey := os.Getenv("GEOCODING_API_KEY")

	http.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]string{"status": "healthy", "service": "api-geo"})
	})

	http.HandleFunc("/geocode/", func(w http.ResponseWriter, r *http.Request) {
		city := r.URL.Path[len("/geocode/"):]
		// Stub geocoding (would call external API with apiKey)
		result := GeoResult{
			City:      city,
			Lat:       47.6062,
			Lng:       -122.3321,
			Source:    "stub",
			APIKeySet: apiKey != "",
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(result)
	})

	fmt.Printf("api-geo listening on :%s\n", port)
	http.ListenAndServe(":"+port, nil)
}
