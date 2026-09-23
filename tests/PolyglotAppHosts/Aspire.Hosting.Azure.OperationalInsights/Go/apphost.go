package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	logAnalytics := builder.AddAzureLogAnalyticsWorkspace("logs")
	_ = logAnalytics.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		workspace := infrastructure.GetOperationalInsightsWorkspace()
		workspace.Tags().Set("provisioning-proxy", "go")
	})
	logAnalytics.WithUrl("https://example.local/logs")
	if logAnalytics.Err() != nil {
		log.Fatalf(aspire.FormatError(logAnalytics.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
