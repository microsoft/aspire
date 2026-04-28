# Aspire Python validation AppHost
# Validates the unified YARP route helpers against Python SDK generation.

from aspire_app import create_builder


def configure_proxy(config):
    endpoint = backend.get_endpoint("http")
    endpoint_cluster = config.add_cluster_from_endpoint(endpoint)
    resource_cluster = config.add_cluster_from_resource(backend_service)
    external_service_cluster = config.add_cluster_from_external_service(external_backend)
    single_destination_cluster = config.add_cluster_with_destination("single-destination", "https://example.net")
    multi_destination_cluster = config.add_cluster_with_destinations("multi-destination", [
        "https://example.org",
        "https://example.edu",
    ])

    config.add_route("/from-endpoint/{**catchall}", endpoint).with_transform({
        "PathPrefix": "/endpoint",
    })
    config.add_route("/from-resource/{**catchall}", backend_service).with_transform({
        "PathPrefix": "/resource",
    })
    config.add_route("/from-external/{**catchall}", external_backend).with_transform({
        "PathPrefix": "/external",
    })
    config.add_route("/from-string/{**catchall}", "https://example.route").with_transform({
        "PathPrefix": "/string",
    })
    config.add_catch_all_route(endpoint_cluster).with_transform({
        "PathPrefix": "/cluster",
    })
    config.add_catch_all_route(endpoint).with_transform({
        "PathPrefix": "/catchall-endpoint",
    })
    config.add_catch_all_route(backend_service).with_transform({
        "PathPrefix": "/catchall-resource",
    })
    config.add_catch_all_route(external_backend).with_transform({
        "PathPrefix": "/catchall-external",
    })
    config.add_catch_all_route("https://example.catchall").with_transform({
        "PathPrefix": "/catchall-string",
    })

    config.add_route("/resource/{**catchall}", resource_cluster)
    config.add_route("/external/{**catchall}", external_service_cluster)
    config.add_route("/single/{**catchall}", single_destination_cluster)
    config.add_route("/multi/{**catchall}", multi_destination_cluster)


with create_builder() as builder:
    build_version = builder.add_parameter_from_config("buildVersion", "MyConfig:BuildVersion")
    build_secret = builder.add_parameter_from_config("buildSecret", "MyConfig:Secret")
    static_files_source = builder.add_container("static-files-source", "nginx")
    backend = builder.add_container("backend", "nginx").with_http_endpoint(name="http", target_port=80)
    backend_service = builder.add_project("backend-service", "./src/BackendService", launch_profile_name="http")
    external_backend = builder.add_external_service("external-backend", "https://example.com")
    external_backend.with_http_health_check()
    proxy = builder.add_yarp("proxy")
    proxy.with_host_port(port=8080)
    proxy.with_host_https_port(port=8443)
    proxy.with_volume()
    proxy.with_build_arg("BUILD_VERSION", build_version)
    proxy.with_build_secret("MY_SECRET", build_secret)
    proxy.publish_with_static_files(static_files_source)
    proxy.with_config(configure_proxy)
    proxy.publish_as_connection_string()
    builder.run()
