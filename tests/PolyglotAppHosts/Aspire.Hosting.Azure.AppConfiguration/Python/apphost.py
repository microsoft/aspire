# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    app_config = builder.add_azure_app_configuration("resource")
    app_config.with_app_configuration_role_assignments()
    app_config.run_as_emulator()
    builder.run()
