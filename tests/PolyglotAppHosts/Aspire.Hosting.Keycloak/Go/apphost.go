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

    adminUsername := builder.AddParameter("keycloak-admin-user")
    adminPassword := builder.AddParameterWithOpts("keycloak-admin-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	keycloak := builder.AddKeycloakWithOpts("resource", &aspire.AddKeycloakOptions{
        Port: aspire.Float64Ptr(8080),
        AdminUsername: adminUsername,
        AdminPassword: adminPassword,
    })

    keycloak.
        WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("keycloak-data")}).
        WithRealmImport(".").
        WithEnabledFeatures([]string{"token-exchange", "opentelemetry"}).
        WithDisabledFeatures([]string{"admin-fine-grained-authz"}).
        WithOtlpExporter()

    keycloak2 := builder.AddKeycloak("resource2").
        WithDataBindMount(".").
        WithRealmImport(".").
        WithEnabledFeatures([]string{"rolling-updates"}).
        WithDisabledFeatures([]string{"scripts"}).
        WithOtlpExporterWithProtocol(aspire.OtlpProtocolHttpProtobuf)

    builder.AddContainer("consumer", "nginx").
        WithReference(keycloak).
        WithReference(keycloak2)

	_, _ = keycloak.Name()
	_, _ = keycloak.Entrypoint()
	_, _ = keycloak.ShellExecution()
	if err = keycloak.Err(); err != nil {
		log.Fatalf("keycloak: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
