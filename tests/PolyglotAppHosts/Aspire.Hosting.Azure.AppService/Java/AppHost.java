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
            .withParameter("applicationInsightsLocation", applicationInsightsLocation)
            .withAzureApplicationInsights(existingApplicationInsights)
            .withDeploymentSlot(deploymentSlot)
            .withDeploymentSlot("staging");
        // The plan's identifier has an _asplan suffix, unlike the hosting resource.
        var planIdentifier = environment.getBicepIdentifier() + "_asplan";
        environment.configureInfrastructure(infrastructure -> {
            var plan = infrastructure.getAppServicePlanByIdentifier(planIdentifier);
            plan.setIsPerSiteScaling(true);
            var _perSiteScaling = plan.isPerSiteScaling();
            // Keep SKU location metadata out of the deployment's plan SKU.
            var sku = infrastructure.createAppServiceSkuDescription();
            sku.locations().add(infrastructure.createAppServiceAzureLocation("westus2"));
            var location = sku.locations().get(0);
            var _locationName = location.name();
        });
        var website = builder.addContainer("frontend", "nginx");
        website.skipEnvironmentVariableNameChecks();
        website.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions()
            .configure((infrastructure, appService) -> {
                var site = infrastructure.getWebSiteByIdentifier("webapp");
                site.setIsHttpsOnly(true);
                var _httpsOnly = site.isHttpsOnly();
                var siteConfig = new AzureAppServiceSiteConfig();
                siteConfig.setIsAlwaysOn(true);
                appService.configureSiteConfig(siteConfig);
            })
            .configureSlot((_infrastructure, appServiceSlot) -> {
                var siteConfig = new AzureAppServiceSiteConfig();
                siteConfig.setIsAlwaysOn(false);
                appServiceSlot.configureSlotSiteConfig(siteConfig);
            }));

        var worker = builder.addExecutable("worker", "dotnet", ".", new String[] { "run" });
        worker.skipEnvironmentVariableNameChecks();
        worker.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions()
            .configure((_infrastructure, appService) -> {
                var siteConfig = new AzureAppServiceSiteConfig();
                siteConfig.setIsAlwaysOn(true);
                appService.configureSiteConfig(siteConfig);
            }));

        var api = builder.addProject("api", "../Fake.Api/Fake.Api.csproj");
        api.skipEnvironmentVariableNameChecks();
        api.publishAsAzureAppServiceWebsite(new PublishAsAzureAppServiceWebsiteOptions()
            .configureSlot((_infrastructure, appServiceSlot) -> {
                var siteConfig = new AzureAppServiceSiteConfig();
                siteConfig.setIsAlwaysOn(false);
                appServiceSlot.configureSlotSiteConfig(siteConfig);
            }));
        var _environmentName = environment.getResourceName();
        var _websiteName = website.getResourceName();
        builder.build().run();
    }
