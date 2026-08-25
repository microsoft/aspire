import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Application Insights validation
        // Exercises exported members of Aspire.Hosting.Azure.ApplicationInsights
        var builder = DistributedApplication.CreateBuilder();
        // addAzureApplicationInsights - factory method with just a name
        var appInsights = builder.addAzureApplicationInsights("insights");
        appInsights.configureInfrastructure((infrastructure) -> {
            var component = infrastructure.getApplicationInsightsComponent();
            component.tags().set("provisioning-proxy", "java");
            var identity = infrastructure.addUserAssignedIdentity("validationIdentity");
            var identityReference = infrastructure.bicep().resourceIdentifier(identity);
            var identityProperties = infrastructure.bicep().member(identityReference, "properties");
            var principalId = infrastructure.bicep().member(identityProperties, "principalId");
            var role = infrastructure.getApplicationInsightsBuiltInRoleMonitoringMetricsPublisher();
            var roleAssignment = component.createRoleAssignment(
                role,
                RoleManagementPrincipalType.SERVICE_PRINCIPAL,
                principalId,
                "java");
            roleAssignment.setDescription("Polyglot provisioning validation");
            roleAssignment.roleDefinitionId();
            roleAssignment.addTo(infrastructure);
        });
        // addAzureLogAnalyticsWorkspace - from the OperationalInsights dependency
        var logAnalytics = builder.addAzureLogAnalyticsWorkspace("logs");
        logAnalytics.configureInfrastructure((infrastructure) -> {
            var workspace = infrastructure.getOperationalInsightsWorkspace();
            workspace.tags().set("provisioning-proxy", "java");
        });
        // withLogAnalyticsWorkspace - fluent method to associate a workspace
        var appInsightsWithWorkspace = builder
          .addAzureApplicationInsights("insights-with-workspace")
          .withLogAnalyticsWorkspace(logAnalytics);
        builder.build().run();
    }
