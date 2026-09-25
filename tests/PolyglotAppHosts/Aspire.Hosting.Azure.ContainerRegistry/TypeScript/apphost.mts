import { AzureContainerRegistryRole, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const registry = await builder.addAzureContainerRegistry("containerregistry")
    .withPurgeTask("0 1 * * *", {
        filter: "samples:*",
        ago: 7,
        keep: 5,
        taskName: "purge-samples"
    });
await registry.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getContainerRegistryService();
    const tags = await service.tags.get();
    await tags.set("provisioning-proxy", "typescript");

    const task = await infrastructure.addContainerRegistryTask("manualPurgeTask");
    await task.parent.set(service);
    await task.name.set("manual-purge-task");
    const step = await infrastructure.createContainerRegistryEncodedTaskStep();
    await step.encodedTaskContent.set("c3RlcHM6IFtd");
    await task.step.set(step);
    const _tasks = await infrastructure.getContainerRegistryTasks();
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
