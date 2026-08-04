// Aspire TypeScript AppHost - Validation for Aspire.Hosting.Azure.Sandboxes

import { AzureSandboxTier, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const sandboxes = await builder.addAzureSandboxGroup("sandboxes");
await sandboxes.withUserAssignedIdentity(
    await builder.addAzureUserAssignedIdentity("sandbox-identity"));

const api = await builder
    .addContainer("api", "mcr.microsoft.com/dotnet/runtime-deps:10.0")
    .withHttpEndpoint({ name: "http", targetPort: 8080 })
    .withExternalHttpEndpoints();

await api.publishAsAzureSandbox(sandboxes, {
    tier: AzureSandboxTier.Large,
    autoSuspendEnabled: true,
    autoSuspendInterval: 900,
    autoSuspendMode: "Disk",
    endpoints: [
        {
            name: "http",
            anonymous: true
        }
    ]
});

await builder.build().run();
