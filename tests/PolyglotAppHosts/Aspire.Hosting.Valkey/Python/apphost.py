# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    password = builder.add_parameter("parameter")
    valkey = builder.add_valkey("resource")
    # ---- Property access on ValkeyResource ----
    _endpoint = valkey.primary_endpoint
    _host = valkey.host
    _port = valkey.port
    _uri = valkey.uri_expression
    _cstr = valkey.connection_string_expression
    builder.run()
