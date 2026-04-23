import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var compose = builder.addDockerComposeEnvironment("compose");
        var containerName = builder.addParameter("container-name");
        var api = builder.addContainer("api", "nginx:alpine");
        api.withBindMount("/host/path/data", "/container/data");
        api.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(80.0));
        var apiEndpoint = api.getEndpoint("http");
        var hostAddressExpression = compose.getHostAddressExpression(apiEndpoint);
        var _hostAddressValueExpression = hostAddressExpression.getValue();
        compose.withProperties((environment) -> {
            environment.setDefaultNetworkName("validation-network");
            var _defaultNetworkName = environment.defaultNetworkName();
            environment.setDashboardEnabled(true);
            var _dashboardEnabled = environment.dashboardEnabled();
            var _environmentName = environment.name();
        });
        compose.configureEnvFile((envVars) -> {
            var bindMount = envVars.get("API_BINDMOUNT_0");
            bindMount.setDescription("Customized bind mount source");
            var _bindMountDescription = bindMount.description();
            bindMount.setDefaultValue("./data");
            var _bindMountDefaultValue = bindMount.defaultValue();
        });
        compose.configureComposeFile((composeFile) -> {
            composeFile.setName("validation-compose");
            var _composeFileName = composeFile.name();
            var composeApi = composeFile.services().get("api");
            composeApi.setPullPolicy("always");
            var _composeApiPullPolicy = composeApi.pullPolicy();
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
            service.setContainerName(containerName.asEnvironmentPlaceholder(composeService));
            service.setRestart("unless-stopped");
            var _composeServiceName = composeService.name();
            var composeEnvironment = composeService.parent();
            var _composeEnvironmentName = composeEnvironment.name();
            var _serviceContainerName = service.containerName();
            var _serviceRestart = service.restart();
        });
        var _resolvedDefaultNetworkName = compose.defaultNetworkName();
        var _resolvedDashboardEnabled = compose.dashboardEnabled();
        var _resolvedName = compose.name();
        builder.build().run();
    }
