import { createBuilder, OtlpProtocol } from './.modules/aspire.js';

const builder = await createBuilder();

const adminUsername = await builder.addParameter("keycloak-admin-user");
const adminPassword = await builder.addParameter("keycloak-admin-password", { secret: true });

const keycloak = await builder.addKeycloak("keycloak", {
    port: 8080,
    adminUsername,
    adminPassword
});

await keycloak
    .withDataVolume({ name: "keycloak-data" })
    .withRealmImport(".")
    .withEnabledFeatures(["token-exchange", "opentelemetry"])
    .withDisabledFeatures(["admin-fine-grained-authz"])
    .withOtlpExporter();

const keycloak2 = await builder.addKeycloak("keycloak2")
    .withDataBindMount(".")
    .withRealmImport(".")
    .withEnabledFeatures(["rolling-updates"])
    .withDisabledFeatures(["scripts"])
    .withOtlpExporter({ protocol: OtlpProtocol.HttpProtobuf });

await builder.addContainer("consumer", "nginx")
    .withReference(keycloak)
    .withReference(keycloak2);

const _keycloakName = await keycloak.name();
const _keycloakEntrypoint = await keycloak.entrypoint.get();
const _keycloakShellExecution = await keycloak.shellExecution.get();

await builder.build().run();
