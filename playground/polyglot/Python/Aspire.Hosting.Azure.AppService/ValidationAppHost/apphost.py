# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    application_insights_location = builder.add_parameter("parameter")
    deployment_slot = builder.add_parameter("parameter")
    existing_application_insights = builder.add_azure_application_insights("resource")
    env = builder.add_azure_app_service_environment("resource")
    website = builder.add_container("resource", "image")
    builder.add_executable("resource", "echo", ".", [])
    builder.add_project("resource", ".", "default")
    _environment_name = env.get_resource_name()
    _website_name = website.get_resource_name()
    builder.run()
