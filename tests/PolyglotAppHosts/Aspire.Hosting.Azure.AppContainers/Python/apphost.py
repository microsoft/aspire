# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, ContainerApp, ContainerAppJob, create_builder


def configure_environment(infrastructure: AzureResourceInfrastructure) -> None:
    environment = infrastructure.get_container_app_managed_env()
    environment.is_zone_redundant = False
    _zone_redundant = environment.is_zone_redundant


def configure_container_app(infrastructure: AzureResourceInfrastructure, app: ContainerApp):
    app.configure_custom_domain(custom_domain, certificate_name)
    app.configure_scale({"MinReplicas": 1})
    provisioned_app = infrastructure.get_container_app_by_identifier("web")
    provisioned_app.workload_profile_name = "consumption"
    _workload_profile = provisioned_app.workload_profile_name


def configure_container_app_job(infrastructure: AzureResourceInfrastructure, job: ContainerAppJob):
    provisioned_job = infrastructure.get_container_app_job_by_identifier(job.bicep_identifier)
    configuration = provisioned_job.config
    configuration.replica_timeout = 300
    _replica_timeout = configuration.replica_timeout
    _job_resource_version = job.resource_version


with create_builder() as builder:
    # Test addAzureContainerAppEnvironment factory method
    env = builder.add_azure_container_app_env("resource")
    env.configure_infrastructure(configure_environment)
    # Test withDashboard with no args (uses default)
    env2 = builder.add_azure_container_app_env("resource")
    env2.with_dashboard()
    # Test withHttpsUpgrade with no args (uses default)
    env2.with_https_upgrade()
    # Test withAzureLogAnalyticsWorkspace with a Log Analytics Workspace resource
    laws = builder.add_azure_log_analytics_workspace("resource")
    env3 = builder.add_azure_container_app_env("resource")
    env3.with_azure_log_analytics_workspace(laws)
    custom_domain = builder.add_parameter("parameter")
    certificate_name = builder.add_parameter("parameter")
    # Test publishAsAzureContainerApp on a container resource with callback
    web = builder.add_container("web", "image")
    web.publish_as_azure_container_app(configure_container_app)
    # Test publishAsAzureContainerAppJob on an executable resource
    api = builder.add_executable("resource", "echo", ".", [])
    api.publish_as_azure_container_app_job()
    # Test publishAsAzureContainerAppJob (parameterless - manual trigger)
    worker = builder.add_container("resource", "image")
    worker.publish_as_azure_container_app_job()
    # Test publishAsAzureContainerAppJob (with callback)
    processor = builder.add_container("resource", "image")
    processor.publish_as_azure_container_app_job(configure=configure_container_app_job)
    # Test publishAsScheduledAzureContainerAppJob (simple - no callback)
    scheduler = builder.add_container("resource", "image")
    scheduler.publish_as_scheduled_azure_container_app_job("0 * * * *")
    # Test publishAsScheduledAzureContainerAppJob (with callback)
    reporter = builder.add_container("resource", "image")
    reporter.publish_as_scheduled_azure_container_app_job("0 0 * * *", configure=configure_container_app_job)
    builder.run()
