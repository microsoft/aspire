// Aspire TypeScript AppHost — Azure Application Insights validation
// Exercises exported members of Aspire.Hosting.Azure.ApplicationInsights

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// addAzureApplicationInsights — factory method with just a name
const appInsights = await builder.addAzureApplicationInsights('insights');

// addAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
const logAnalytics = await builder.addAzureLogAnalyticsWorkspace('logs');

// withLogAnalyticsWorkspace — fluent method to associate a workspace
const appInsightsWithWorkspace = await builder
  .addAzureApplicationInsights('insights-with-workspace')
  .withLogAnalyticsWorkspace(logAnalytics);

await builder.build().run();