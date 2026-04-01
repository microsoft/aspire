import { AzureContainerRegistryRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const registry = await builder.addAzureContainerRegistry("containerregistry")
    .withPurgeTask("0 1 * * *", {
        filter: "samples:*",
        ago: 7,
        keep: 5,
        taskName: "purge-samples"
    });

const environment = await builder.addAzureContainerAppEnvironment("environment");
await environment.withAzureContainerRegistry(registry);
await environment.withContainerRegistryRoleAssignments(registry, [
    AzureContainerRegistryRole.AcrPull,
    AzureContainerRegistryRole.AcrPush
]);

const registryFromEnvironment = await environment.getAzureContainerRegistry();
await registryFromEnvironment.withPurgeTask("0 2 * * *", {
    filter: "environment:*",
    ago: 14,
    keep: 2
});

await builder.build().run();
