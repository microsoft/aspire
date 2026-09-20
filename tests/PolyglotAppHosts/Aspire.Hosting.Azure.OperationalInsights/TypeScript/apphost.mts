// Aspire TypeScript AppHost — Azure Operational Insights validation
// Exercises exported members of Aspire.Hosting.Azure.OperationalInsights

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// addAzureLogAnalyticsWorkspace
const logAnalytics = await builder.addAzureLogAnalyticsWorkspace('logs');
await logAnalytics.configureInfrastructure(async infrastructure => {
    const workspace = await infrastructure.getOperationalInsightsWorkspace();
    const tags = await workspace.tags.get();
    await tags.set("provisioning-proxy", "typescript");
});

// Fluent call on the returned resource builder
await logAnalytics.withUrl('https://example.local/logs');

await builder.build().run();
