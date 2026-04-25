package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	kusto := builder.AddAzureKustoCluster("kusto").
		RunAsEmulator(&aspire.RunAsEmulatorOptions{
			ConfigureContainer: func(emulator aspire.AzureKustoEmulatorResource) {
				emulator.WithHostPort(8088)
			},
		})
	if kusto.Err() != nil {
		log.Fatalf(aspire.FormatError(kusto.Err()))
	}

	defaultDatabase := kusto.AddReadWriteDatabase("samples")
	if defaultDatabase.Err() != nil {
		log.Fatalf(aspire.FormatError(defaultDatabase.Err()))
	}
	customDatabase := kusto.AddReadWriteDatabase("analytics", &aspire.AddReadWriteDatabaseOptions{
		DatabaseName: aspire.StringPtr("AnalyticsDb"),
	})
	if customDatabase.Err() != nil {
		log.Fatalf(aspire.FormatError(customDatabase.Err()))
	}

	defaultDatabase.WithCreationScript(".create database Samples ifnotexists")
	customDatabase.WithCreationScript(".create database AnalyticsDb ifnotexists")

	_, _ = kusto.IsEmulator()
	_ = kusto.UriExpression()
	_ = kusto.ConnectionStringExpression()
	_ = kusto.NameOutputReference()
	_ = kusto.ClusterUri()

	_, _ = defaultDatabase.DatabaseName()
	_ = defaultDatabase.Parent()
	_ = defaultDatabase.ConnectionStringExpression()
	_, _ = defaultDatabase.GetDatabaseCreationScript()

	_, _ = customDatabase.DatabaseName()
	_ = customDatabase.Parent()
	_ = customDatabase.ConnectionStringExpression()
	_, _ = customDatabase.GetDatabaseCreationScript()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
