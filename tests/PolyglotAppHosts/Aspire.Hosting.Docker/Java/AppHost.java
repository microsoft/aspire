import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var compose = builder.addDockerComposeEnvironment("compose");
        var api = builder.addContainer("api", "nginx:alpine");
        compose.withProperties((environment) -> {
            environment.setDefaultNetworkName("validation-network");
            var _defaultNetworkName = environment.defaultNetworkName();
            environment.setDashboardEnabled(true);
            var _dashboardEnabled = environment.dashboardEnabled();
            var _environmentName = environment.name();
        });
        compose.withDashboard(false);
        compose.withDashboard();
        compose.configureDashboard((dashboard) -> {
            dashboard.withHostPort(18888.0);
            dashboard.withForwardedHeaders(true);
            var _dashboardName = dashboard.name();
            var primaryEndpoint = dashboard.primaryEndpoint();
            var _primaryUrl = primaryEndpoint.url();
            var _primaryHost = primaryEndpoint.host();
            var _primaryPort = primaryEndpoint.port();
            var otlpGrpcEndpoint = dashboard.otlpGrpcEndpoint();
            var _otlpGrpcUrl = otlpGrpcEndpoint.url();
            var _otlpGrpcPort = otlpGrpcEndpoint.port();
        });
        api.publishAsDockerComposeService((composeService, service) -> {
            service.setContainerName("validation-api");
            service.setPullPolicy("always");
            service.setRestart("unless-stopped");
            var _composeServiceName = composeService.name();
            var composeEnvironment = composeService.parent();
            var _composeEnvironmentName = composeEnvironment.name();
            var _serviceContainerName = service.containerName();
            var _servicePullPolicy = service.pullPolicy();
            var _serviceRestart = service.restart();
        });
        var _resolvedDefaultNetworkName = compose.defaultNetworkName();
        var _resolvedDashboardEnabled = compose.dashboardEnabled();
        var _resolvedName = compose.name();
        builder.build().run();
    }
