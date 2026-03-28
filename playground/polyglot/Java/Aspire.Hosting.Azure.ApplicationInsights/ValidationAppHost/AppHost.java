import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Application Insights validation
        // Exercises exported members of Aspire.Hosting.Azure.ApplicationInsights
        var builder = DistributedApplication.CreateBuilder();
        // addAzureApplicationInsights - factory method with just a name
        var appInsights = builder.addAzureApplicationInsights("insights");
        // addAzureLogAnalyticsWorkspace - from the OperationalInsights dependency
        var logAnalytics = builder.addAzureLogAnalyticsWorkspace("logs");
        // withLogAnalyticsWorkspace - fluent method to associate a workspace
        var appInsightsWithWorkspace = builder
          .addAzureApplicationInsights("insights-with-workspace")
          .withLogAnalyticsWorkspace(logAnalytics);
        builder.build().run();
    }
