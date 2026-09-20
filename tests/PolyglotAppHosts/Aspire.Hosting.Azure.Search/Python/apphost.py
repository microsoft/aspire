# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    service = infrastructure.get_search_service()
    service.tags.set("provisioning-proxy", "python")


with create_builder() as builder:
    search = builder.add_azure_search("resource")
    search.configure_infrastructure(configure_provisioning)
    search.with_search_role_assignments(search, ["SearchIndexDataReader"])
    builder.run()
