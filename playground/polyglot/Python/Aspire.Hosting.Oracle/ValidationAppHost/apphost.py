# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ---- addOracle: factory method with defaults ----
    oracle = builder.add_oracle("resource")
    # ---- addOracle: factory method with custom password and port ----
    custom_password = builder.add_parameter("parameter")
    oracle2 = builder.add_oracle("resource")
    # ---- addDatabase: child resource with default databaseName ----
    db = oracle.add_database("resource")
    # ---- addDatabase: child resource with explicit databaseName ----
    db2 = oracle.add_database("resource")
    # ---- withDataVolume: data persistence (default name) ----
    oracle.with_data_volume()
    # ---- withDataVolume: data persistence (custom name) ----
    oracle2.with_data_volume()
    # ---- withDataBindMount: bind mount for data ----
    oracle2.with_data_bind_mount()
    # ---- withInitFiles: initialization scripts ----
    oracle2.with_init_files()
    # ---- withDbSetupBindMount: DB setup directory ----
    oracle2.with_db_setup_bind_mount()
    # ---- withReference: connection string reference (from core) ----
    other_oracle = builder.add_oracle("resource")
    other_db = other_oracle.add_database("resource")
    oracle.with_reference()
    # ---- withReference: with connection name option ----
    oracle.with_reference()
    # ---- withReference: unified reference to another Oracle server resource ----
    oracle.with_reference()
    # ---- Fluent chaining: multiple methods chained ----
    oracle3 = builder.add_oracle("resource")
    oracle3.add_database("resource")
    # ---- Property access on OracleDatabaseServerResource ----
    _endpoint = oracle.primary_endpoint
    _host = oracle.host
    _port = oracle.port
    _user_name_ref = oracle.user_name_reference
    _uri = oracle.uri_expression
    _jdbc = oracle.jdbc_connection_string
    _cstr = oracle.connection_string_expression
    # ---- Property access on OracleDatabaseResource ----
    _db_name = db.database_name
    _db_uri = db.uri_expression
    _db_jdbc = db.jdbc_connection_string
    _db_parent = db.parent
    _db_cstr = db.connection_string_expression
    builder.run()
