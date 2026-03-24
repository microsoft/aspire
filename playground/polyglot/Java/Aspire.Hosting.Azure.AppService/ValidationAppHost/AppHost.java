import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var applicationInsightsLocation = builder.addParameter("applicationInsightsLocation");
        var deploymentSlot = builder.addParameter("deploymentSlot");
        var existingApplicationInsights = builder.addAzureApplicationInsights("existingApplicationInsights");
        var environment = builder.addAzureAppServiceEnvironment("appservice-environment")
            .withDashboard()
            .withDashboard(false)
            .withAzureApplicationInsights()
            .withAzureApplicationInsightsLocation("westus")
            .withAzureApplicationInsightsLocationParameter(applicationInsightsLocation)
            .withAzureApplicationInsightsResource(existingApplicationInsights)
            .withDeploymentSlotParameter(deploymentSlot)
            .withDeploymentSlot("staging");
        var website = builder.addContainer("frontend", "nginx");
        website.skipEnvironmentVariableNameChecks();
        website.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions().configure((_infrastructure, _appService) -> {}).configureSlot((_infrastructure, _appServiceSlot) -> {}));

        var worker = builder.addExecutable("worker", "dotnet", ".", new String[] { "run" });
        worker.skipEnvironmentVariableNameChecks();
        worker.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions().configure((_infrastructure, _appService) -> {}));

        var api = builder.addProject("api", "../Fake.Api/Fake.Api.csproj", "https");
        api.skipEnvironmentVariableNameChecks();
        api.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions().configureSlot((_infrastructure, _appServiceSlot) -> {}));
        var _environmentName = environment.getResourceName();
        var _websiteName = website.getResourceName();
        builder.build().run();
    }
