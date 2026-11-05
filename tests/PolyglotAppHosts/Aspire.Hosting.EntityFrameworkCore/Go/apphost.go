package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	api := builder.AddProject("api", "./src/Api")
	dotnetAPI := builder.AddDotnetProject("dotnet-api", "./src/Api/Api.csproj")
	apiMigrations := builder.AddProject("api-migrations-project", "./src/Api.Migrations")

	dotnetAPI.AddEFMigrations("dotnet-api-migrations")
	api.AddEFMigrations("api-migrations").
		WithMigrationsProject(&aspire.WithMigrationsProjectOptions{
			MigrationsProject: "./src/Api.Migrations",
		})
	api.AddEFMigrations("api-dbcontext-migrations", &aspire.AddEFMigrationsOptions{
		DbContextTypeName: aspire.StringPtr("Api.Data.AppDbContext"),
	}).WithMigrationsProject(&aspire.WithMigrationsProjectOptions{
		MigrationsProject: apiMigrations,
	}).
		WithMigrationNamespace("Api.Data.Migrations")
	api.AddEFMigrations("api-script-migrations").
		PublishAsMigrationScript()
	api.AddEFMigrations("api-script-options-migrations").
		PublishAsMigrationScript(&aspire.PublishAsMigrationScriptOptions{
			Idempotent:     aspire.BoolPtr(false),
			NoTransactions: aspire.BoolPtr(true),
		})
	api.AddEFMigrations("api-bundle-migrations").
		PublishAsMigrationBundle()
	api.AddEFMigrations("api-bundle-options-migrations").
		PublishAsMigrationBundle(&aspire.PublishAsMigrationBundleOptions{
			TargetRuntime:    aspire.StringPtr("linux-x64"),
			SelfContained:    aspire.BoolPtr(true),
			PublishContainer: aspire.BoolPtr(true),
			BaseImage:        aspire.StringPtr("mcr.microsoft.com/dotnet/runtime:10.0"),
		})

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
