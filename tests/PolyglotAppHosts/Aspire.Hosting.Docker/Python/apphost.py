# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    compose = builder.add_docker_compose_environment("compose")
    api = builder.add_container("api", "nginx:alpine")
    api.with_bind_mount("/host/path/data", "/container/data")

    def configure_environment(environment):
        environment.default_network_name = "validation-network"
        _default_network_name = environment.default_network_name

        environment.dashboard_enabled = True
        _dashboard_enabled = environment.dashboard_enabled

        _environment_name = environment.name

    compose.with_properties(configure_environment)

    def configure_env_file(env_vars):
        bind_mount = env_vars["API_BINDMOUNT_0"]
        bind_mount.description = "Customized bind mount source"
        _bind_mount_description = bind_mount.description
        bind_mount.default_value = "./data"
        _bind_mount_default_value = bind_mount.default_value

    compose.configure_env_file(configure_env_file)
    compose.with_dashboard()
    compose.with_dashboard()
    compose.configure_dashboard()
    api.publish_as_docker_compose_service()
    _resolved_default_network_name = compose.default_network_name
    _resolved_dashboard_enabled = compose.dashboard_enabled
    _resolved_name = compose.name
    builder.run()
