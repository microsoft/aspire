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

	weatherApi := builder.AddProject("weatherapi", "./src/WeatherApi",
		&aspire.AddProjectOptions{LaunchProfileOrOptions: "http"})
	timeApi := builder.AddProject("timeapi", "./src/TimeApi",
		&aspire.AddProjectOptions{LaunchProfileOrOptions: "https"})
	timeApi.WithHttpsEndpoint(&aspire.WithHttpsEndpointOptions{Name: aspire.StringPtr("api")})

	blazorApp := builder.AddBlazorWasmProject("app", "./src/Client/Client.csproj")
	blazorApp.WithReference(weatherApi.GetEndpoint("http"))
	blazorApp.WithReference(timeApi.GetEndpoint("api"))

	protocol := aspire.OtlpProtocolHttpProtobuf
	gateway := builder.AddBlazorGateway("gateway").
		WithExternalHttpEndpoints().
		WithOtlpExporter(&aspire.WithOtlpExporterOptions{Protocol: &protocol})
	gateway.WithBlazorClientApp(blazorApp)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
