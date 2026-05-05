import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var adminUsername = builder.addParameter("keycloak-admin-user");
        var adminPassword = builder.addParameter("keycloak-admin-password", new AddParameterOptions().secret(true));
        var keycloak = builder.addKeycloak("keycloak", new AddKeycloakOptions().port(8080.0));
        keycloak
            .withDataVolume("keycloak-data")
            .withRealmImport(".")
            .withEnabledFeatures(new String[] { "token-exchange", "opentelemetry" })
            .withDisabledFeatures(new String[] { "admin-fine-grained-authz" })
            .withOtlpExporter();
        var keycloak2 = builder.addKeycloak("keycloak2")
            .withDataBindMount(".")
            .withRealmImport(".")
            .withEnabledFeatures(new String[] { "rolling-updates" })
            .withDisabledFeatures(new String[] { "scripts" })
            .withOtlpExporter(OtlpProtocol.HTTP_PROTOBUF);
        var consumer = builder.addContainer("consumer", "nginx");
        consumer.withReference(keycloak, new WithReferenceOptions());
        consumer.withReference(keycloak2, new WithReferenceOptions());
        var _keycloakName = keycloak.name();
        var _keycloakEntrypoint = keycloak.entrypoint();
        var _keycloakShellExecution = keycloak.shellExecution();
        builder.build().run();
    }
