# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    server = infrastructure.get_postgre_sql_flexible_server()
    backup = server.backup
    backup.backup_retention_days = 14
    _backup_retention = backup.backup_retention_days


with create_builder() as builder:
    # 1) addAzurePostgresFlexibleServer — main factory method
    pg = builder.add_azure_postgres_flexible_server("resource")
    pg.configure_infrastructure(configure_provisioning)
    # 2) addDatabase — child resource
    db = pg.add_database("resource")
    # 3) withPasswordAuthentication — configures password auth (auto KeyVault)
    pg_auth = builder.add_azure_postgres_flexible_server("resource")
    pg_auth.with_password_authentication()
    # 4) runAsContainer — run as local PostgreSQL container
    pg_container = builder.add_azure_postgres_flexible_server("resource")
    pg_container.run_as_container()
    # 5) addDatabase on container-mode server
    db_container = pg_container.add_database("resource")
    app = builder.build()
    builder.run()
