// Aspire TypeScript AppHost — Azure Application Insights validation
// Exercises exported members of Aspire.Hosting.Azure.ApplicationInsights

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// addAzureApplicationInsights — factory method with just a name
const appInsights = await builder.addAzureApplicationInsights('insights');
await appInsights.configureInfrastructure(async infrastructure => {
    const component = await infrastructure.getApplicationInsightsComponent();
    const tags = await component.tags.get();
    await tags.set("provisioning-proxy", "typescript");
});

// addAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
const logAnalytics = await builder.addAzureLogAnalyticsWorkspace('logs');

// withLogAnalyticsWorkspace — fluent method to associate a workspace
const appInsightsWithWorkspace = await builder
  .addAzureApplicationInsights('insights-with-workspace')
  .withLogAnalyticsWorkspace(logAnalytics);

await builder.build().run();