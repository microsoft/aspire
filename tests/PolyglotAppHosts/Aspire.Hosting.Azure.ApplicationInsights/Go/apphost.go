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

	// AddAzureApplicationInsights — factory method with just a name
	appInsights := builder.AddAzureApplicationInsights("insights")
	if err := appInsights.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
	logAnalytics := builder.AddAzureLogAnalyticsWorkspace("logs")
	if err := logAnalytics.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	appInsightsWithWorkspace := builder.AddAzureApplicationInsights("insights-with-workspace").
		WithLogAnalyticsWorkspace(logAnalytics)
	if err = appInsightsWithWorkspace.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
