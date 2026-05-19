import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const compose = await builder.addDockerComposeEnvironment("compose");
const containerName = await builder.addParameter("container-name");
const api = await builder.addContainer("api", "nginx:alpine");
await api.withComputeEnvironment(compose);
await api.withBindMount("/host/path/data", "/container/data");
await api.withHttpEndpoint({ name: "http", targetPort: 80 });

const apiEndpoint = await api.getEndpoint("http");
const hostAddressExpression = await compose.getHostAddressExpression(apiEndpoint);
const _hostAddressValueExpression: string | null = await hostAddressExpression.getValue();

await compose.withProperties(async (environment) => {
    await environment.defaultNetworkName.set("validation-network");
    const _defaultNetworkName: string | null = await environment.defaultNetworkName.get();

    await environment.dashboardEnabled.set(true);
    const _dashboardEnabled: boolean | null = await environment.dashboardEnabled.get();

    const _environmentName: string = await environment.name();
});

await compose.configureEnvFile(async (envVars) => {
    const bindMount = await envVars.get("API_BINDMOUNT_0");
    await bindMount.description.set("Customized bind mount source");
    const _bindMountDescription: string | null = await bindMount.description.get();
    await bindMount.defaultValue.set("./data");
    const _bindMountDefaultValue: string | null = await bindMount.defaultValue.get();
});

await compose.configureComposeFile(async (composeFile) => {
    await composeFile.name.set("validation-compose");
    const _composeFileName: string | null = await composeFile.name.get();

    await composeFile.addNetwork("validation-network-extra", { driver: "bridge" });
    await composeFile.addService("validation-sidecar", { image: "busybox" });
    await composeFile.addVolume("validation-data", {
        driver: "local",
        configure: async (volume) => {
            await volume.labels.set("purpose", "validation");
        }
    });
    await composeFile.addConfig("validation-config", { content: "enabled=true" });
    await composeFile.addSecret("validation-secret", { external: true });

    const composeApi = await composeFile.services.get("api");
    await composeApi.pullPolicy.set("always");
    const _composeApiPullPolicy: string | null = await composeApi.pullPolicy.get();

    await composeApi.addVolume("validation-data", "/container/compose-data", { isReadOnly: true });
});

await compose.withDashboard({ enabled: false });
await compose.withDashboard();

await compose.configureDashboard(async (dashboard) => {
    await dashboard.withHostPort({ port: 18888 });
    await dashboard.withForwardedHeaders({ enabled: true });

    const _dashboardName: string = await dashboard.name();

    const primaryEndpoint = await dashboard.primaryEndpoint();
    const _primaryUrl: string = await primaryEndpoint.url();
    const _primaryHost: string = await primaryEndpoint.host();
    const _primaryPort: number = await primaryEndpoint.port();

    const otlpGrpcEndpoint = await dashboard.otlpGrpcEndpoint();
    const _otlpGrpcUrl: string = await otlpGrpcEndpoint.url();
    const _otlpGrpcPort: number = await otlpGrpcEndpoint.port();
});

await api.publishAsDockerComposeService(async (composeService, service) => {
    await service.containerName.set(await containerName.asEnvironmentPlaceholder(composeService));
    await service.restart.set("unless-stopped");

    const _composeServiceName: string = await composeService.name();
    const composeEnvironment = await composeService.parent();
    const _composeEnvironmentName: string = await composeEnvironment.name();

    const _serviceContainerName: string | null = await service.containerName.get();
    const _serviceRestart: string | null = await service.restart.get();
    const _serviceConfigsCount: number = await service.configs.count();
    const _serviceSecretsCount: number = await service.secrets.count();
    const _serviceUlimitsCount: number = await service.ulimits.count();
});

const _resolvedDefaultNetworkName: string | null = await compose.defaultNetworkName.get();
const _resolvedDashboardEnabled: boolean | null = await compose.dashboardEnabled.get();
const _resolvedName: string = await compose.name();

await builder.build().run();
