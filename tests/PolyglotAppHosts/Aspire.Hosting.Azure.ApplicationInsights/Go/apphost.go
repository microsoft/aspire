package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// AddAzureApplicationInsights — factory method with just a name
	appInsights := builder.AddAzureApplicationInsights("insights")
	_ = appInsights.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		component := infrastructure.GetApplicationInsightsComponent()
		component.Tags().Set("provisioning-proxy", "go")
		identity := infrastructure.AddApplicationInsightsUserAssignedIdentity("validationIdentity")
		principalID, err := identity.PrincipalId()
		if err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		role := infrastructure.GetApplicationInsightsBuiltInRoleMonitoringMetricsPublisher()
		roleAssignment := component.CreateRoleAssignment(
			role,
			aspire.RoleManagementPrincipalTypeServicePrincipal,
			principalID,
			"go")
		roleAssignment.SetDescription("Polyglot provisioning validation")
		_ = roleAssignment.RoleDefinitionId()
		if err := roleAssignment.AddTo(infrastructure); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	if err := appInsights.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
	logAnalytics := builder.AddAzureLogAnalyticsWorkspace("logs")
	_ = logAnalytics.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		workspace := infrastructure.GetOperationalInsightsWorkspace()
		workspace.Tags().Set("provisioning-proxy", "go")
	})
	if err := logAnalytics.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	appInsightsWithWorkspace := builder.AddAzureApplicationInsights("insights-with-workspace").
		WithLogAnalyticsWorkspace(logAnalytics)
	if err = appInsightsWithWorkspace.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
