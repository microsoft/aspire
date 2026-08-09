import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Operational Insights validation
        // Exercises exported members of Aspire.Hosting.Azure.OperationalInsights
        var builder = DistributedApplication.CreateBuilder();
        // addAzureLogAnalyticsWorkspace
        var logAnalytics = builder.addAzureLogAnalyticsWorkspace("logs");
        // Fluent call on the returned resource builder
        logAnalytics.withUrl("https://example.local/logs", null);
        builder.build().run();
    }
