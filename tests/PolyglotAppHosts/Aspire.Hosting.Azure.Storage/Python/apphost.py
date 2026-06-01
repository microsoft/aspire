# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    storage = builder.add_azure_storage("resource")
    storage.run_as_emulator()
    storage.with_storage_role_assignments()
    # });
    storage.add_blobs("resource")
    storage.add_tables("resource")
    storage.add_queues("resource")
    storage.add_queue("resource")
    storage.add_blob_container("resource")
    builder.run()
