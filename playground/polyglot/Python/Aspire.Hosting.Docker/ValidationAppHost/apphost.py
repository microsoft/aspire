# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    compose = builder.add_docker_compose_environment("resource")
    api = builder.add_container("resource", "image")
    compose.with_properties()
    compose.with_dashboard()
    compose.with_dashboard()
    compose.configure_dashboard()
    api.publish_as_docker_compose_service()
    _resolved_default_network_name = compose.default_network_name
    _resolved_dashboard_enabled = compose.dashboard_enabled
    _resolved_name = compose.name
    builder.run()
