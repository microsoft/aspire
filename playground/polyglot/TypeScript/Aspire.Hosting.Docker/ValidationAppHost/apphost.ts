import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const compose = await builder.addDockerComposeEnvironment("compose");
const api = await builder.addContainer("api", "nginx:alpine");

await compose.withProperties(async (environment) => {
    await environment.defaultNetworkName.set("validation-network");
    const _defaultNetworkName: string = await environment.defaultNetworkName.get();

    await environment.dashboardEnabled.set(true);
    const _dashboardEnabled: boolean = await environment.dashboardEnabled.get();

    const _environmentName: string = await environment.name.get();
});

await compose.withDashboard({ enabled: false });
await compose.withDashboard();

await compose.configureDashboard(async (dashboard) => {
    await dashboard.withHostPort({ port: 18888 });
    await dashboard.withForwardedHeaders({ enabled: true });

    const _dashboardName: string = await dashboard.name.get();

    const primaryEndpoint = await dashboard.primaryEndpoint.get();
    const _primaryUrl: string = await primaryEndpoint.url.get();
    const _primaryHost: string = await primaryEndpoint.host.get();
    const _primaryPort: number = await primaryEndpoint.port.get();

    const otlpGrpcEndpoint = await dashboard.otlpGrpcEndpoint.get();
    const _otlpGrpcUrl: string = await otlpGrpcEndpoint.url.get();
    const _otlpGrpcPort: number = await otlpGrpcEndpoint.port.get();
});

await api.publishAsDockerComposeService(async (composeService, service) => {
    await service.containerName.set("validation-api");
    await service.pullPolicy.set("always");
    await service.restart.set("unless-stopped");

    const _composeServiceName: string = await composeService.name.get();
    const composeEnvironment = await composeService.parent.get();
    const _composeEnvironmentName: string = await composeEnvironment.name.get();

    const _serviceContainerName: string = await service.containerName.get();
    const _servicePullPolicy: string = await service.pullPolicy.get();
    const _serviceRestart: string = await service.restart.get();
});

const _resolvedDefaultNetworkName: string = await compose.defaultNetworkName.get();
const _resolvedDashboardEnabled: boolean = await compose.dashboardEnabled.get();
const _resolvedName: string = await compose.name.get();

await builder.build().run();
