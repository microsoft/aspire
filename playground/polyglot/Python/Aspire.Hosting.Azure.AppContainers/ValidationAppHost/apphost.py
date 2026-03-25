# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Test addAzureContainerAppEnvironment factory method
    env = builder.add_azure_container_app_environment("resource")
    # Test withDashboard with no args (uses default)
    env2 = builder.add_azure_container_app_environment("resource")
    env2.with_dashboard()
    # Test withHttpsUpgrade with no args (uses default)
    env2.with_https_upgrade()
    # Test withAzureLogAnalyticsWorkspace with a Log Analytics Workspace resource
    laws = builder.add_azure_log_analytics_workspace("resource")
    env3 = builder.add_azure_container_app_environment("resource")
    env3.with_azure_log_analytics_workspace()
    # Test publishAsAzureContainerApp on a container resource with callback
    web = builder.add_container("resource", "image")
    web.publish_as_azure_container_app()
    # Test publishAsAzureContainerAppJob on an executable resource
    api = builder.add_executable("resource", "echo", ".", [])
    api.publish_as_azure_container_app_job()
    # Test publishAsAzureContainerAppJob (parameterless - manual trigger)
    worker = builder.add_container("resource", "image")
    worker.publish_as_azure_container_app_job()
    # Test publishAsConfiguredAzureContainerAppJob (with callback)
    processor = builder.add_container("resource", "image")
    processor.publish_as_configured_azure_container_app_job()
    # Test publishAsScheduledAzureContainerAppJob (simple - no callback)
    scheduler = builder.add_container("resource", "image")
    scheduler.publish_as_scheduled_azure_container_app_job()
    # Test publishAsConfiguredScheduledAzureContainerAppJob (with callback)
    reporter = builder.add_container("resource", "image")
    reporter.publish_as_configured_scheduled_azure_container_app_job()
    builder.run()
