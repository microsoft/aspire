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

	compose := builder.AddDockerComposeEnvironment("compose")
	api := builder.AddContainer("api", "nginx:alpine")
	api.WithComputeEnvironment(compose)

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

	compose.ConfigureComposeFile(func(composeFile aspire.ComposeFile) {
		composeFile.SetName("validation-compose")
		_, _ = composeFile.Name()
		composeFile.AddNetwork("validation-network-extra", &aspire.AddNetworkOptions{Driver: aspire.StringPtr("bridge")})
		composeFile.AddService("validation-sidecar", &aspire.AddServiceOptions{Image: aspire.StringPtr("busybox")})
		composeFile.AddVolume("validation-data", &aspire.AddVolumeOptions{Driver: aspire.StringPtr("local")})
		composeFile.AddConfig("validation-config", &aspire.AddConfigOptions{Content: aspire.StringPtr("enabled=true")})
		composeFile.AddSecret("validation-secret", &aspire.AddSecretOptions{External: aspire.BoolPtr(true)})
		composeApi, _ := composeFile.Services().Get("api")
		composeApi.SetPullPolicy("always")
		_, _ = composeApi.PullPolicy()
		composeApi.AddVolume("validation-data", "/container/compose-data", &aspire.ServiceAddVolumeOptions{IsReadOnly: aspire.BoolPtr(true)})
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
		_, _ = service.Configs().Count()
		_, _ = service.Secrets().Count()
		_, _ = service.Ulimits().Count()
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
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
