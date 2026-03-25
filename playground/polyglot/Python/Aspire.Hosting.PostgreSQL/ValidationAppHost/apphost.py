# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ---- AddPostgres: factory method ----
    postgres = builder.add_postgres("resource")
    # ---- AddDatabase: child resource ----
    db = postgres.add_database("resource")
    # ---- WithPgAdmin: management UI ----
    postgres.with_pg_admin()
    postgres.with_pg_admin()
    # ---- WithPgWeb: management UI ----
    postgres.with_pg_web()
    postgres.with_pg_web()
    # ---- WithDataVolume: data persistence ----
    postgres.with_data_volume()
    postgres.with_data_volume()
    # ---- WithDataBindMount: bind mount ----
    postgres.with_data_bind_mount()
    postgres.with_data_bind_mount()
    # ---- WithInitFiles: initialization scripts ----
    postgres.with_init_files()
    # ---- WithHostPort: explicit port for PostgreSQL ----
    postgres.with_host_port()
    # ---- WithCreationScript: custom database creation SQL ----
    db.with_creation_script()
    # ---- WithPassword / WithUserName: credential configuration ----
    custom_password = builder.add_parameter("parameter")
    custom_user = builder.add_parameter("parameter")
    pg2 = builder.add_postgres("resource")
    pg2.with_password()
    pg2.with_user_name()
    # ---- Property access on PostgresServerResource ----
    _endpoint = postgres.primary_endpoint
    _name_ref = postgres.user_name_reference
    _uri = postgres.uri_expression
    _jdbc = postgres.jdbc_connection_string
    _cstr = postgres.connection_string_expression
    # ---- Property access on PostgresDatabaseResource ----
    _db_name = db.database_name
    _db_uri = db.uri_expression
    _db_jdbc = db.jdbc_connection_string
    _db_cstr = db.connection_string_expression
    builder.run()
