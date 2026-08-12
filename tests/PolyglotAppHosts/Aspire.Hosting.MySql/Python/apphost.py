# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    root_password = builder.add_parameter("parameter")
    mysql = builder.add_my_sql("resource")
    mysql.with_php_my_admin()
    db = mysql.add_database("resource")
    db.with_creation_script()
    # ---- Property access on MySqlServerResource ----
    _endpoint = mysql.primary_endpoint
    _host = mysql.host
    _port = mysql.port
    _uri = mysql.uri_expression
    _jdbc = mysql.jdbc_connection_string
    _cstr = mysql.connection_string_expression
    _databases = None
    builder.run()
