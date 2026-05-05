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

	search := builder.AddAzureSearch("resource")
	search.WithRoleAssignments(search, []aspire.AzureSearchRole{
		aspire.AzureSearchRoleSearchServiceContributor,
		aspire.AzureSearchRoleSearchIndexDataReader})
	if search.Err() != nil {
		log.Fatalf(aspire.FormatError(search.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
