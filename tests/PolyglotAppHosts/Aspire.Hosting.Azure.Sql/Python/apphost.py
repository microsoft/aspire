# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    storage = builder.add_azure_storage("resource")
    sql_server = builder.add_azure_sql_server("resource")
    db = sql_server.add_database("resource")
    db2 = sql_server.add_database("resource")
    db2.with_default_azure_sku()
    sql_server.run_as_container()
    sql_server.with_admin_deployment_script_storage()
    _db3 = sql_server.add_database("resource")
    _host_name = sql_server.host_name
    _port = sql_server.port
    _uri_expression = sql_server.uri_expression
    _connection_string_expression = sql_server.connection_string_expression
    _jdbc_connection_string = sql_server.jdbc_connection_string
    _is_container = sql_server.is_container
    _database_count = sql_server.databases.count()
    _has_my_db = sql_server.databases.contains_key()
    _parent = db.parent
    _db_connection_string_expression = db.connection_string_expression
    _database_name = db.database_name
    _db_is_container = db.is_container
    _db_uri_expression = db.uri_expression
    _db_jdbc_connection_string = db.jdbc_connection_string
    builder.run()
