# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


def configure_provisioning(infrastructure):
    workspace = infrastructure.get_operational_insights_workspace()
    workspace.tags.set("provisioning-proxy", "python")


with create_builder() as builder:
    # addAzureLogAnalyticsWorkspace
    log_analytics = builder.add_azure_log_analytics_workspace("resource")
    log_analytics.configure_infrastructure(configure_provisioning)
    # Fluent call on the returned resource builder
    log_analytics.with_url("http://localhost")
    builder.run()
