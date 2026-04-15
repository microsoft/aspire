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

	// ── 1. addDockerComposeEnvironment ───────────────────────────────────────
	compose := builder.AddDockerComposeEnvironment("compose")
	api := builder.AddContainer("api", "nginx:alpine")

	// ── 2. withProperties (typed callback) ────────────────────────────────────
	compose.WithProperties(func(environment *aspire.DockerComposeEnvironmentResource) {
		environment.SetDefaultNetworkName("validation-network")
		_, _ = environment.DefaultNetworkName()
		environment.SetDashboardEnabled(true)
		_, _ = environment.DashboardEnabled()
		_, _ = environment.Name()
	})

	// ── 3. withDashboard (WithOpts + simple) ──────────────────────────────────
	compose.WithDashboardWithOpts(&aspire.WithDashboardOptions{Enabled: aspire.BoolPtr(false)})
	compose.WithDashboard()

	// ── 4. configureDashboard (typed callback) ─────────────────────────────────
	compose.ConfigureDashboard(func(dashboard *aspire.DockerComposeAspireDashboardResource) {
		dashboard.WithHostPortWithOpts(&aspire.WithHostPortOptions{Port: aspire.Float64Ptr(18888)})
		dashboard.WithForwardedHeadersWithOpts(&aspire.WithForwardedHeadersOptions{Enabled: aspire.BoolPtr(true)})

		_, _ = dashboard.Name()

		primaryEndpoint, _ := dashboard.PrimaryEndpoint()
		if primaryEndpoint != nil {
			_, _ = primaryEndpoint.Url()
			_, _ = primaryEndpoint.Host()
			_, _ = primaryEndpoint.Port()
		}

		otlpGrpcEndpoint, _ := dashboard.OtlpGrpcEndpoint()
		if otlpGrpcEndpoint != nil {
			_, _ = otlpGrpcEndpoint.Url()
			_, _ = otlpGrpcEndpoint.Port()
		}
	})

	// ── 5. publishAsDockerComposeService (two-param typed callback) ───────────
	_, _ = api.PublishAsDockerComposeService(func(composeService *aspire.DockerComposeServiceResource, service *aspire.Service) {
		service.SetContainerName("validation-api")
		service.SetPullPolicy("always")
		service.SetRestart("unless-stopped")

		_, _ = composeService.Name()
		composeEnv, _ := composeService.Parent()
		if composeEnv != nil {
			_, _ = composeEnv.Name()
		}

		_, _ = service.ContainerName()
		_, _ = service.PullPolicy()
		_, _ = service.Restart()
	})

	// ── 6. property getters ───────────────────────────────────────────────────
	_, _ = compose.DefaultNetworkName()
	_, _ = compose.DashboardEnabled()
	_, _ = compose.Name()
	if err = compose.Err(); err != nil {
		log.Fatalf("compose: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
