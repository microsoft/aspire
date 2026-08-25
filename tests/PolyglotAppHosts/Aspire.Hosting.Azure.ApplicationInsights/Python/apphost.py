# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    component = infrastructure.get_app_insights_component()
    component.tags.set("provisioning-proxy", "python")
    identity = infrastructure.add_user_assigned_identity("validationIdentity")
    role = infrastructure.get_app_insights_built_in_role_monitoring_metrics_publisher()
    role_assignment = component.create_role_assignment(
        role,
        "ServicePrincipal",
        identity.principal_id,
        "python",
    )
    role_assignment.description = "Polyglot provisioning validation"
    _role_definition_id = role_assignment.role_definition_id
    role_assignment.add_to(infrastructure)


def configure_log_analytics(infrastructure: AzureResourceInfrastructure) -> None:
    workspace = infrastructure.get_operational_insights_workspace()
    workspace.tags.set("provisioning-proxy", "python")


with create_builder() as builder:
    # addAzureApplicationInsights — factory method with just a name
    app_insights = builder.add_azure_app_insights("resource")
    app_insights.configure_infrastructure(configure_provisioning)
    # addAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
    log_analytics = builder.add_azure_log_analytics_workspace("resource")
    log_analytics.configure_infrastructure(configure_log_analytics)
    # withLogAnalyticsWorkspace — fluent method to associate a workspace
    app_insights_with_workspace = builder.add_azure_app_insights(
        "resource-with-workspace"
    ).with_log_analytics_workspace(log_analytics)
    builder.run()
