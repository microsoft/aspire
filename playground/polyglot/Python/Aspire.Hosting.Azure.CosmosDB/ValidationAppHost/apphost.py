# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # 1) addAzureCosmosDB
    cosmos = builder.add_azure_cosmos_db("resource")
    # 2) withDefaultAzureSku
    cosmos.with_default_azure_sku()
    # 3) addCosmosDatabase
    db = cosmos.add_cosmos_database("resource")
    # 4) addContainer (single partition key path)
    db.add_container("resource", "image")
    # 5) addContainerWithPartitionKeyPaths (IEnumerable<string> export)
    db.add_container_with_partition_key_paths("resource")
    # 6) withAccessKeyAuthentication
    cosmos.with_access_key_authentication()
    # 7) withAccessKeyAuthenticationWithKeyVault
    key_vault = builder.add_azure_key_vault("resource")
    cosmos.with_access_key_authentication_with_key_vault()
    # 8) runAsEmulator + emulator container configuration methods
    cosmos_emulator = builder.add_azure_cosmos_db("resource")
    cosmos_emulator.run_as_emulator()
    # 12) runAsPreviewEmulator + 13) withDataExplorer
    cosmos_preview = builder.add_azure_cosmos_db("resource")
    cosmos_preview.run_as_preview_emulator()
    app = builder.build()
    builder.run()
