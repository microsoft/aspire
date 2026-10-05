# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    cluster = infrastructure.get_kusto_cluster()
    cluster.is_streaming_ingest_enabled = True
    _streaming_ingest = cluster.is_streaming_ingest_enabled
    database = infrastructure.add_kusto_read_write_database("proxyDatabase")
    # Python exposes the inherited properties on the base proxy.
    base_database = infrastructure.get_kusto_database_by_identifier("proxyDatabase")
    base_database.parent = cluster
    base_database.name = "ProxyDatabase"
    _database_name = base_database.name
    # Exercise the derived AddTo export; adding to the same infrastructure is idempotent.
    database.add_to(infrastructure)


with create_builder() as builder:
    kusto = builder.add_azure_kusto_cluster("resource")
    kusto.configure_infrastructure(configure_provisioning)
    default_database = kusto.add_read_write_database("resource")
    custom_database = kusto.add_read_write_database("resource")
    default_database.with_creation_script(".create table Events (Id: string)")
    custom_database.with_creation_script(".create table Metrics (Name: string, Value: real)")
    _is_emulator = kusto.is_emulator
    _cluster_uri = kusto.uri_expression
    _cluster_connection_string = kusto.connection_string_expression
    _default_database_name = default_database.database_name
    _default_database_parent = default_database.parent
    _default_database_connection_string = default_database.connection_string_expression
    _default_database_creation_script = default_database.get_database_creation_script()
    _custom_database_name = custom_database.database_name
    _custom_database_parent = custom_database.parent
    _custom_database_connection_string = custom_database.connection_string_expression
    _custom_database_creation_script = custom_database.get_database_creation_script()
    builder.run()
