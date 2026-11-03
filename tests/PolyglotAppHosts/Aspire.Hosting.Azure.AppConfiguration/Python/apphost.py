# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    store = infrastructure.get_app_config_store()
    store.disable_local_auth = True
    _disable_local_auth = store.disable_local_auth


with create_builder() as builder:
    app_config = builder.add_azure_app_config("resource")
    app_config.configure_infrastructure(configure_provisioning)
    app_config.with_app_config_role_assignments(
        app_config,
        ["AppConfigurationDataOwner", "AppConfigurationDataReader"],
    )
    app_config.run_as_emulator()
    builder.run()
