# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    account = infrastructure.get_storage_account()
    account.tags.set("provisioning-proxy", "python")
    immutability_policy = infrastructure.create_account_immutability_policy()
    immutability_policy.immutability_period_since_creation_in_days = 30


with create_builder() as builder:
    storage = builder.add_azure_storage("resource")
    storage.configure_infrastructure(configure_provisioning)
    storage.run_as_emulator()
    storage.with_storage_role_assignments(storage, ["StorageBlobDataReader"])
    # });
    storage.add_blobs("resource")
    storage.add_tables("resource")
    storage.add_queues("resource")
    storage.add_queue("resource")
    storage.add_blob_container("resource")
    builder.run()
