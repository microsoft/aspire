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

	funcApp := builder.AddAzureFunctionsProject("myfunc", "../MyFunctions/MyFunctions.csproj")

	storage := builder.AddAzureStorage("funcstorage")
	funcApp.WithHostStorage(storage)

	chainedFunc := builder.AddAzureFunctionsProject("chained-func", "../OtherFunc/OtherFunc.csproj")
	chainedFunc.WithHostStorage(storage)
	chainedFunc.WithEnvironment("MY_KEY", "my-value")
	chainedFunc.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(7071)})

	anotherStorage := builder.AddAzureStorage("appstorage")
	funcApp.WithReference(aspire.NewIResource(anotherStorage.Handle(), anotherStorage.Client()))

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
