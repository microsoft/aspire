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

	adminUsername := builder.AddParameter("keycloak-admin-user")
	adminPassword := builder.AddParameter("keycloak-admin-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	keycloak := builder.AddKeycloak("keycloak", &aspire.AddKeycloakOptions{
		Port:          aspire.Float64Ptr(8080),
		AdminUsername: &adminUsername,
		AdminPassword: &adminPassword,
	})

	keycloak.
		WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("keycloak-data")}).
		WithRealmImport(".").
		WithEnabledFeatures([]string{"token-exchange", "opentelemetry"}).
		WithDisabledFeatures([]string{"admin-fine-grained-authz"}).
		WithOtlpExporter()

	protocol := aspire.OtlpProtocolHttpProtobuf
	keycloak2 := builder.AddKeycloak("keycloak2").
		WithDataBindMount(".").
		WithRealmImport(".").
		WithEnabledFeatures([]string{"rolling-updates"}).
		WithDisabledFeatures([]string{"scripts"}).
		WithOtlpExporter(&aspire.WithOtlpExporterOptions{Protocol: &protocol})

	builder.AddContainer("consumer", "nginx").
		WithReference(keycloak).
		WithReference(keycloak2)

	_, _ = keycloak.Name()
	_, _ = keycloak.Entrypoint()
	_, _ = keycloak.ShellExecution()
	if err = keycloak.Err(); err != nil {
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
