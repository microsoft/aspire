# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    admin_username = builder.add_parameter("parameter")
    admin_password = builder.add_parameter("parameter")
    keycloak = builder.add_keycloak("resource")
    keycloak2 = builder.add_keycloak("resource")
    builder.add_container("resource", "image")
    _keycloak_name = keycloak.name
    _keycloak_entrypoint = keycloak.entrypoint
    _keycloak_shell_execution = keycloak.shell_execution
    builder.run()
