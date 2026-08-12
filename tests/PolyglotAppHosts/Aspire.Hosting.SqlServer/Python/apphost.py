# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Test 1: Basic SQL Server resource creation (addSqlServer)
    sql_server = builder.add_sql_server("resource")
    # Test 2: Add database to SQL Server (addDatabase)
    sql_server.add_database("resource")
    # Test 3: Test withDataVolume
    builder.add_sql_server("resource")
    # Test 4: Test withHostPort
    builder.add_sql_server("resource")
    # Test 5: Test password parameter with addParameter
    custom_password = builder.add_parameter("parameter")
    builder.add_sql_server("resource")
    # Test 6: Chained configuration - multiple With* methods
    sql_chained = builder.add_sql_server("resource")
    # Test 7: Add multiple databases to same server
    sql_chained.add_database("resource")
    sql_chained.add_database("resource")
    # ---- Property access on SqlServerServerResource ----
    _endpoint = sql_server.primary_endpoint
    _host = sql_server.host
    _port = sql_server.port
    _uri = sql_server.uri_expression
    _jdbc = sql_server.jdbc_connection_string
    _user_name = sql_server.user_name_reference
    # Build and run the app
    _cstr = sql_server.connection_string_expression
    _databases = None
    builder.run()
