# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, WebSite, WebSiteSlot, create_builder


def configure_environment(infrastructure: AzureResourceInfrastructure) -> None:
    # The plan's identifier has an _asplan suffix, unlike the hosting resource.
    plan = infrastructure.get_app_service_plan_by_identifier(plan_identifier)
    plan.is_per_site_scaling = True
    _per_site_scaling = plan.is_per_site_scaling
    # Keep SKU location metadata out of the deployment's plan SKU.
    sku = infrastructure.create_app_service_sku_description()
    sku.locations.add(infrastructure.create_app_service_azure_location("westus2"))
    location = sku.locations.get(0)
    _location_name = location.name
    # Mutate a detached connection model; networking output lists stay read-only.
    connection = infrastructure.create_remote_private_endpoint_connection()
    addresses = connection.i_p_addresses
    addresses.add("192.0.2.1")
    addresses.insert(0, "2001:db8::1")
    addresses.set(1, "192.0.2.2")
    literal = addresses.get(0)
    addresses.set(1, literal)
    bicep = infrastructure.bicep()
    # Index creates an untyped expression rather than a typed BicepValue<string>.
    expression = bicep.index(bicep.parse_json(bicep.string('["192.0.2.3"]')), 0)
    addresses.add(expression)
    addresses.insert(1, expression)
    addresses.set(0, addresses.get(3))
    assert addresses.count == 4
    assert [addresses.get(index).kind for index in range(4)] == [
        "Expression",
        "Expression",
        "Literal",
        "Expression",
    ]
    networking = infrastructure.create_ase_v3_networking_config_data()
    for output in [
        networking.external_inbound_ip_addresses,
        networking.internal_inbound_ip_addresses,
        networking.linux_outbound_ip_addresses,
        networking.windows_outbound_ip_addresses,
    ]:
        assert output.count == 0


def configure_app_service(infrastructure: AzureResourceInfrastructure, app_service: WebSite):
    app_service.configure_site_config({"IsAlwaysOn": True})
    site = infrastructure.get_web_site_by_identifier("webapp")
    site.is_https_only = True
    _https_only = site.is_https_only


def configure_app_service_slot(_infrastructure: AzureResourceInfrastructure, app_service_slot: WebSiteSlot):
    app_service_slot.configure_slot_site_config({"IsAlwaysOn": False})


with create_builder() as builder:
    application_insights_location = builder.add_parameter("parameter")
    deployment_slot = builder.add_parameter("parameter")
    existing_application_insights = builder.add_azure_app_insights("resource")
    env = builder.add_azure_app_service_env("resource")
    plan_identifier = f"{env.get_bicep_identifier()}_asplan"
    env.configure_infrastructure(configure_environment)
    website = builder.add_container("resource", "image")
    website.publish_as_azure_app_service_website(
        configure=configure_app_service,
        configure_slot=configure_app_service_slot,
    )
    builder.add_executable("resource", "echo", ".", []).publish_as_azure_app_service_website(
        configure=configure_app_service
    )
    builder.add_project("resource", ".", launch_profile_or_options="default").publish_as_azure_app_service_website(
        configure_slot=configure_app_service_slot
    )
    _environment_name = env.get_resource_name()
    _website_name = website.get_resource_name()
    builder.run()
