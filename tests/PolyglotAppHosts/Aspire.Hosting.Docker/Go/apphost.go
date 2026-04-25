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

	compose := builder.AddDockerComposeEnvironment("compose")
	api := builder.AddContainer("api", "nginx:alpine")

	compose.WithProperties(func(environment aspire.DockerComposeEnvironmentResource) {
		environment.SetDefaultNetworkName("validation-network")
		_, _ = environment.DefaultNetworkName()
		environment.SetDashboardEnabled(true)
		_, _ = environment.DashboardEnabled()
		_, _ = environment.Name()
	})

	compose.WithDashboard(&aspire.WithDashboardOptions{Enabled: aspire.BoolPtr(false)})
	compose.WithDashboard()

	compose.ConfigureDashboard(func(dashboard aspire.DockerComposeAspireDashboardResource) {
		dashboard.WithHostPort(&aspire.WithHostPortOptions{Port: aspire.Float64Ptr(18888)})
		dashboard.WithForwardedHeaders(&aspire.WithForwardedHeadersOptions{Enabled: aspire.BoolPtr(true)})

		_, _ = dashboard.Name()

		primaryEndpoint := dashboard.PrimaryEndpoint()
		_, _ = primaryEndpoint.Url()
		_, _ = primaryEndpoint.Host()
		_, _ = primaryEndpoint.Port()

		otlpGrpcEndpoint := dashboard.OtlpGrpcEndpoint()
		_, _ = otlpGrpcEndpoint.Url()
		_, _ = otlpGrpcEndpoint.Port()
	})

	_ = api.PublishAsDockerComposeService(func(composeService aspire.DockerComposeServiceResource, service aspire.Service) {
		service.SetContainerName("validation-api")
		service.SetPullPolicy("always")
		service.SetRestart("unless-stopped")

		_, _ = composeService.Name()
		composeEnv := composeService.Parent()
		_, _ = composeEnv.Name()

		_, _ = service.ContainerName()
		_, _ = service.PullPolicy()
		_, _ = service.Restart()
	})

	_, _ = compose.DefaultNetworkName()
	_, _ = compose.DashboardEnabled()
	_, _ = compose.Name()
	if err = compose.Err(); err != nil {
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
