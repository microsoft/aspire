# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    custom_api_key = builder.add_parameter("parameter")
    builder.add_qdrant("resource")
    qdrant = builder.add_qdrant("resource")
    qdrant.with_data_volume()
    consumer = builder.add_container("resource", "image")
    consumer.with_reference()
    # ---- Property access on QdrantServerResource ----
    _endpoint = qdrant.primary_endpoint
    _grpc_host = qdrant.grpc_host
    _grpc_port = qdrant.grpc_port
    _http_endpoint = qdrant.http_endpoint
    _http_host = qdrant.http_host
    _http_port = qdrant.http_port
    _uri = qdrant.uri_expression
    _http_uri = qdrant.http_uri_expression
    _cstr = qdrant.connection_string_expression
    builder.run()
