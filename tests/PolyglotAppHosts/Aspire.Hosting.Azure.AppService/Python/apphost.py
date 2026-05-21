# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


def configure_app_service(_infrastructure, app_service):
    app_service.configure_site_config({"IsAlwaysOn": True})


def configure_app_service_slot(_infrastructure, app_service_slot):
    app_service_slot.configure_slot_site_config({"IsAlwaysOn": False})


with create_builder() as builder:
    application_insights_location = builder.add_parameter("parameter")
    deployment_slot = builder.add_parameter("parameter")
    existing_application_insights = builder.add_azure_application_insights("resource")
    env = builder.add_azure_app_service_environment("resource")
    website = builder.add_container("resource", "image")
    website.publish_as_azure_app_service_website(
        configure=configure_app_service,
        configure_slot=configure_app_service_slot,
    )
    builder.add_executable("resource", "echo", ".", []).publish_as_azure_app_service_website(
        configure=configure_app_service
    )
    builder.add_project("resource", ".", launch_profile_name="default").publish_as_azure_app_service_website(
        configure_slot=configure_app_service_slot
    )
    _environment_name = env.get_resource_name()
    _website_name = website.get_resource_name()
    builder.run()
