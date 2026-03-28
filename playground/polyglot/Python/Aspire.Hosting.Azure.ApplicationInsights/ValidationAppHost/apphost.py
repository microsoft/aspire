# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addAzureApplicationInsights — factory method with just a name
    app_insights = builder.add_azure_application_insights("resource")
    # addAzureLogAnalyticsWorkspace — from the OperationalInsights dependency
    log_analytics = builder.add_azure_log_analytics_workspace("resource")
    # withLogAnalyticsWorkspace — fluent method to associate a workspace
    app_insights_with_workspace = builder
    builder.run()
