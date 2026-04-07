// Aspire TypeScript AppHost — Azure Operational Insights validation
// Exercises exported members of Aspire.Hosting.Azure.OperationalInsights

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// addAzureLogAnalyticsWorkspace
const logAnalytics = await builder.addAzureLogAnalyticsWorkspace('logs');

// Fluent call on the returned resource builder
await logAnalytics.withUrl('https://example.local/logs');

await builder.build().run();
