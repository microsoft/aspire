# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    cache = builder.add_garnet("resource")
    # ---- Property access on GarnetResource ----
    garnet = cache
    _endpoint = garnet.primary_endpoint
    _host = garnet.host
    _port = garnet.port
    _uri = garnet.uri_expression
    _cstr = garnet.connection_string_expression
    builder.run()
