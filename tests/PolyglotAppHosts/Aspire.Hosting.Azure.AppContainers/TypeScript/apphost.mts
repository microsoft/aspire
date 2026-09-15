// Aspire TypeScript AppHost - Validation for Aspire.Hosting.Azure.AppContainers
// For more information, see: https://aspire.dev

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// === Azure Container App Environment ===
// Test addAzureContainerAppEnvironment factory method
const env = await builder.addAzureContainerAppEnvironment("myenv");
await env.configureInfrastructure(async infrastructure => {
    const environment = await infrastructure.getContainerAppManagedEnvironment();
    await environment.isZoneRedundant.set(false);
    const _zoneRedundant = await environment.isZoneRedundant.get();
});

// Test fluent chaining on AzureContainerAppEnvironmentResource
await env
    .withAzdResourceNaming()
    .withCompactResourceNaming()
    .withDashboard({ enable: true })
    .withHttpsUpgrade({ upgrade: false });

// Test withDashboard with no args (uses default)
const env2 = await builder.addAzureContainerAppEnvironment("myenv2");
await env2.withDashboard();

// Test withHttpsUpgrade with no args (uses default)
await env2.withHttpsUpgrade();

// === WithAzureLogAnalyticsWorkspace ===
// Test withAzureLogAnalyticsWorkspace with a Log Analytics Workspace resource
const laws = await builder.addAzureLogAnalyticsWorkspace("laws");
const env3 = await builder.addAzureContainerAppEnvironment("myenv3");
await env3.withAzureLogAnalyticsWorkspace(laws);
const customDomain = await builder.addParameter("customDomain");
const certificateName = await builder.addParameter("certificateName");

// === PublishAsAzureContainerApp ===
// Test publishAsAzureContainerApp on a container resource with callback
const web = await builder.addContainer("web", "myregistry/web:latest");
await web.publishAsAzureContainerApp(async (infrastructure, app) => {
    await app.configureCustomDomain(customDomain, certificateName);
    await app.configureScale({ minReplicas: 1 });
    const provisionedApp = await infrastructure.getContainerAppByIdentifier("web");
    await provisionedApp.workloadProfileName.set("consumption");
    const _workloadProfile = await provisionedApp.workloadProfileName.get();
});

// Test publishAsAzureContainerAppJob on an executable resource
const api = await builder.addExecutable("api", "dotnet", ".", ["run"]);
await api.publishAsAzureContainerAppJob();

// === PublishAsAzureContainerAppJob ===
// Test publishAsAzureContainerAppJob (parameterless - manual trigger)
const worker = await builder.addContainer("worker", "myregistry/worker:latest");
await worker.publishAsAzureContainerAppJob();

// Test publishAsAzureContainerAppJob (with callback)
const processor = await builder.addContainer("processor", "myregistry/processor:latest");
await processor.publishAsAzureContainerAppJob({
    configure: async (infrastructure, job) => {
        const provisionedJob = await infrastructure.getContainerAppJobByIdentifier("processor");
        const configuration = await provisionedJob.configuration.get();
        await configuration.replicaTimeout.set(300);
        const _replicaTimeout = await configuration.replicaTimeout.get();
        await job.bicepIdentifier.set("processorJob");
    }
});

// Test publishAsScheduledAzureContainerAppJob (simple - no callback)
const scheduler = await builder.addContainer("scheduler", "myregistry/scheduler:latest");
await scheduler.publishAsScheduledAzureContainerAppJob("0 0 * * *");

// Test publishAsScheduledAzureContainerAppJob (with callback)
const reporter = await builder.addContainer("reporter", "myregistry/reporter:latest");
await reporter.publishAsScheduledAzureContainerAppJob("0 */6 * * *", {
    configure: async (_infrastructure, job) => {
        await job.bicepIdentifier.set("reporterJob");
    }
});

await builder.build().run();
