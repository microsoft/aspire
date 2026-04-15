package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	logAnalytics := builder.AddAzureLogAnalyticsWorkspace("logs")
	logAnalytics.WithUrl("https://example.local/logs")
	if err = logAnalytics.Err(); err != nil {
		log.Fatalf("logAnalytics: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
