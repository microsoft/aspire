import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Validation for Aspire.Hosting.Azure.AppContainers
        // For more information, see: https://aspire.dev
        var builder = DistributedApplication.CreateBuilder();
        // === Azure Container App Environment ===
        // Test addAzureContainerAppEnvironment factory method
        var env = builder.addAzureContainerAppEnvironment("myenv");
        // Test fluent chaining on AzureContainerAppEnvironmentResource
        env
            .withAzdResourceNaming()
            .withCompactResourceNaming()
            .withDashboard(true)
            .withHttpsUpgrade(false);
        // Test withDashboard with no args (uses default)
        var env2 = builder.addAzureContainerAppEnvironment("myenv2");
        env2.withDashboard();
        // Test withHttpsUpgrade with no args (uses default)
        env2.withHttpsUpgrade();
        // === WithAzureLogAnalyticsWorkspace ===
        // Test withAzureLogAnalyticsWorkspace with a Log Analytics Workspace resource
        var laws = builder.addAzureLogAnalyticsWorkspace("laws");
        var env3 = builder.addAzureContainerAppEnvironment("myenv3");
        env3.withAzureLogAnalyticsWorkspace(laws);
        var customDomain = builder.addParameter("customDomain");
        var certificateName = builder.addParameter("certificateName");
        // === PublishAsAzureContainerApp ===
        // Test publishAsAzureContainerApp on a container resource with callback
        var web = builder.addContainer("web", "myregistry/web:latest");
        web.publishAsAzureContainerApp((infrastructure, app) -> {
            app.configureCustomDomain(customDomain, certificateName);
        });
        // Test publishAsAzureContainerAppJob on an executable resource
        var api = builder.addExecutable("api", "dotnet", ".", new String[] { "run" });
        api.publishAsAzureContainerAppJob();
        // === PublishAsAzureContainerAppJob ===
        // Test publishAsAzureContainerAppJob (parameterless - manual trigger)
        var worker = builder.addContainer("worker", "myregistry/worker:latest");
        worker.publishAsAzureContainerAppJob();
        // Test publishAsAzureContainerAppJob (with callback)
        var processor = builder.addContainer("processor", "myregistry/processor:latest");
        processor.publishAsAzureContainerAppJob((infrastructure, job) -> {
            // Configure the container app job here
        });
        // Test publishAsScheduledAzureContainerAppJob (simple - no callback)
        var scheduler = builder.addContainer("scheduler", "myregistry/scheduler:latest");
        scheduler.publishAsScheduledAzureContainerAppJob("0 0 * * *");
        // Test publishAsScheduledAzureContainerAppJob (with callback)
        var reporter = builder.addContainer("reporter", "myregistry/reporter:latest");
        reporter.publishAsScheduledAzureContainerAppJob("0 */6 * * *", (infrastructure, job) -> {
                // Configure the scheduled job here
            });
        builder.build().run();
    }
