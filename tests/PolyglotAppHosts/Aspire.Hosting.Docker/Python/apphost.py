# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    compose = builder.add_docker_compose_environment("compose")
    container_name = builder.add_parameter("container-name")
    api = builder.add_container("api", "nginx:alpine")
    api.with_compute_environment(compose)
    api.with_bind_mount("/host/path/data", "/container/data")
    api.with_http_endpoint(name="http", target_port=80)
    api_endpoint = api.get_endpoint("http")
    host_address_expression = compose.get_host_address_expression(api_endpoint)
    _host_address_value_expression = repr(host_address_expression)

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

    def configure_compose_file(compose_file):
        compose_file.name = "validation-compose"
        _compose_file_name = compose_file.name
        compose_file.add_network("validation-network-extra", driver="bridge")
        compose_file.add_service("validation-sidecar", image="busybox")
        compose_file.add_volume("validation-data", driver="local")
        compose_file.add_config("validation-config", content="enabled=true")
        compose_file.add_secret("validation-secret", external=True)
        compose_api = compose_file.services["api"]
        compose_api.pull_policy = "always"
        _compose_api_pull_policy = compose_api.pull_policy
        compose_api.add_volume("validation-data", "/container/compose-data", is_read_only=True)

    def configure_service(compose_service, service):
        service.set_container_name(container_name.as_environment_placeholder(compose_service))
        service.set_restart("unless-stopped")
        compose_service.name()
        compose_service.parent().name()
        service.container_name()
        service.restart()
        _service_configs_count = len(service.configs)
        _service_secrets_count = len(service.secrets)
        _service_ulimits_count = len(service.ulimits)

    compose.configure_compose_file(configure_compose_file)
    compose.with_dashboard()
    compose.with_dashboard()
    compose.configure_dashboard()
    api.publish_as_docker_compose_service(configure_service)
    _resolved_default_network_name = compose.default_network_name
    _resolved_dashboard_enabled = compose.dashboard_enabled
    _resolved_name = compose.name
    builder.run()
