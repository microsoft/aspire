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

	// ── 1. AddAzureFunctionsProject (path-based overload) ───────────────────────
	funcApp := builder.AddAzureFunctionsProject("myfunc", "../MyFunctions/MyFunctions.csproj")
	if funcApp.Err() != nil {
		log.Fatalf(aspire.FormatError(funcApp.Err()))
	}

	// ── 2. WithHostStorage — specify custom Azure Storage for Functions host ────
	storage := builder.AddAzureStorage("funcstorage")
	funcApp.WithHostStorage(storage)

	// ── 3. Fluent chaining — verify return types enable chaining ────────────────
	chainedFunc := builder.
		AddAzureFunctionsProject("chained-func", "../OtherFunc/OtherFunc.csproj").
		WithHostStorage(storage).
		WithEnvironment("MY_KEY", "my-value").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(7071)})
	if chainedFunc.Err() != nil {
		log.Fatalf(aspire.FormatError(chainedFunc.Err()))
	}

	// ── 4. WithReference from base builder — standard resource references ───────
	anotherStorage := builder.AddAzureStorage("appstorage")
	funcApp.WithReference(anotherStorage)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
