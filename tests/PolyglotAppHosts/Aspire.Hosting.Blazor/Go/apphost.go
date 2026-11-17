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

	weatherApi := builder.AddDotnetProject("weatherapi", "./src/WeatherApi",
		&aspire.AddDotnetProjectOptions{Options: &aspire.DotnetProjectOptions{LaunchProfileName: aspire.StringPtr("http")}})
	timeApi := builder.AddDotnetProject("timeapi", "./src/TimeApi",
		&aspire.AddDotnetProjectOptions{Options: &aspire.DotnetProjectOptions{LaunchProfileName: aspire.StringPtr("https")}})
	timeApi.WithHttpsEndpoint(&aspire.WithHttpsEndpointOptions{Name: aspire.StringPtr("api")})

	blazorApp := builder.AddBlazorWasmProject("app", "./src/Client/Client.csproj")
	blazorApp.WithReference(weatherApi.GetEndpoint("http"))
	blazorApp.WithReference(timeApi.GetEndpoint("api"))

	protocol := aspire.OtlpProtocolHttpProtobuf
	gateway := builder.AddBlazorGateway("gateway").
		WithExternalHttpEndpoints().
		WithOtlpExporter(&aspire.WithOtlpExporterOptions{Protocol: &protocol})
	gateway.WithBlazorClientApp(blazorApp)

	dotnetProjectBlazorApp := builder.AddBlazorWasmProject("dotnet-app", "./src/Client/Client.csproj")
	dotnetProjectGateway := builder.AddDotnetProjectBlazorGateway("dotnet-gateway")
	dotnetProjectGateway.WithBlazorClientApp(dotnetProjectBlazorApp)

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
