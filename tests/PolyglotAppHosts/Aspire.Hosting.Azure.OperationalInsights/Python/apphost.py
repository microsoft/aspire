# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addAzureLogAnalyticsWorkspace
    log_analytics = builder.add_azure_log_analytics_workspace("resource")
    # Fluent call on the returned resource builder
    log_analytics.with_url("http://localhost")
    builder.run()
