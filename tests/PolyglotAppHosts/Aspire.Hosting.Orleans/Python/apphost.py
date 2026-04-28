# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    provider = builder.add_connection_string("connection-string", env_var_name="ORLEANS_PROVIDER_CONNECTION_STRING")
    orleans = builder.add_orleans("resource")
    orleans_client = orleans.as_client()
    silo = builder.add_container("resource", "image")
    silo.with_orleans_reference()
    client = builder.add_container("resource", "image")
    client.with_orleans_client_reference()
    builder.run()
