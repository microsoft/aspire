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

	storage := builder.AddAzureStorage("funcstorage")

	// ── 1. AddDotnetAzureFunction and its typed fluent APIs ─────────────────────
	functions := builder.AddDotnetAzureFunction("dotnetfunc", "../MyFunctions/MyFunctions.csproj")
	functions.WithHostStorage(storage)
	functions.WithBuildEnvironment("BUILD_KEY", "build-value")
	functions.WithReplicas(2)
	functions.DisableForwardedHeaders()
	functions.WithEndpointsInEnvironment([]string{"http"})
	if functions.Err() != nil {
		log.Fatal(aspire.FormatError(functions.Err()))
	}

	// ── 2. Legacy AddAzureFunctionsProject compatibility surface ────────────────
	legacyFuncApp := builder.AddAzureFunctionsProject("legacyfunc", "../OtherFunc/OtherFunc.csproj")
	legacyFuncApp.WithHostStorage(storage)
	if legacyFuncApp.Err() != nil {
		log.Fatal(aspire.FormatError(legacyFuncApp.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
