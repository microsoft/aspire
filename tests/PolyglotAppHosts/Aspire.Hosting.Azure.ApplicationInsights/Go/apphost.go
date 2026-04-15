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

	_ = builder.AddAzureApplicationInsights("insights")

	logAnalytics := builder.AddAzureLogAnalyticsWorkspace("logs")

	appInsightsWithWorkspace := builder.AddAzureApplicationInsights("insights-with-workspace")
	appInsightsWithWorkspace.WithLogAnalyticsWorkspace(logAnalytics)
	if err = appInsightsWithWorkspace.Err(); err != nil {
		log.Fatalf("appInsightsWithWorkspace: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
