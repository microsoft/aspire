# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    registry = builder.add_azure_container_registry("resource")
    env = builder.add_azure_container_app_env("resource")
    env.with_azure_container_registry(registry)
    env.with_container_registry_role_assignments(registry, ["AcrPull"])
    registry_from_environment = env.get_azure_container_registry()
    registry_from_environment.with_purge_task("0 0 * * *")
    builder.run()
