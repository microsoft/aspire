// Aspire TypeScript AppHost — Azure Application Insights validation
// Exercises exported members of Aspire.Hosting.Azure.ApplicationInsights

import { createBuilder, RoleManagementPrincipalType } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// addAzureApplicationInsights — factory method with just a name
const appInsights = await builder.addAzureApplicationInsights('insights');
await appInsights.configureInfrastructure(async infrastructure => {
    const component = await infrastructure.getApplicationInsightsComponent();
    const tags = await component.tags.get();
    await tags.set("provisioning-proxy", "typescript");

    const identity = await infrastructure.addApplicationInsightsUserAssignedIdentity("validationIdentity");
    const role = await infrastructure.getApplicationInsightsBuiltInRoleMonitoringMetricsPublisher();
    const roleAssignment = await component.createRoleAssignment(
        role,
        RoleManagementPrincipalType.ServicePrincipal,
        await identity.principalId(),
        "typescript");
    await roleAssignment.description.set("Polyglot provisioning validation");
    const _roleDefinitionId = await roleAssignment.roleDefinitionId.get();
    await roleAssignment.addTo(infrastructure);
});

// addAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
const logAnalytics = await builder.addAzureLogAnalyticsWorkspace('logs');
await logAnalytics.configureInfrastructure(async infrastructure => {
    const workspace = await infrastructure.getOperationalInsightsWorkspace();
    const tags = await workspace.tags.get();
    await tags.set("provisioning-proxy", "typescript");
});

// withLogAnalyticsWorkspace — fluent method to associate a workspace
const appInsightsWithWorkspace = await builder
  .addAzureApplicationInsights('insights-with-workspace')
  .withLogAnalyticsWorkspace(logAnalytics);

await builder.build().run();