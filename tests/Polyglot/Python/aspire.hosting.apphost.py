# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addContainer (pre-existing)
    container = builder.add_container("resource", "image")
    # addDockerfile
    docker_container = builder.add_dockerfile("resource", ".")
    # addExecutable (pre-existing)
    exe = builder.add_executable("resource", "echo", ".", [])
    # addProject (pre-existing)
    project = builder.add_project("resource", ".", "default")
    # addCSharpApp
    csharp_app = builder.add_c_sharp_app("resource", ".")
    # addRedis
    cache = builder.add_redis("resource")
    # addDotnetTool
    tool = builder.add_dotnet_tool("resource", "package")
    # addParameterFromConfiguration
    config_param = builder.add_parameter_from_config("parameter", "Config:Key")
    secret_param = builder.add_parameter_from_config("parameter", "Config:Key")
    # withDockerfileBaseImage
    container.with_dockerfile_base_image()
    # withImageRegistry
    container.with_image_registry("docker.io")
    # ===================================================================
    docker_container.with_http_endpoint()
    endpoint = docker_container.get_endpoint("default")
    expr = "expression"
    built_connection_string = builder.add_connection_string_builder("connection-string", lambda *_args, **_kwargs: None)
    built_connection_string.with_connection_property("Key", "Value")
    built_connection_string.with_connection_property_value("Key", "Value")
    # withEnvironmentEndpoint
    container.with_environment_endpoint("KEY", None)
    # withEnvironmentParameter
    container.with_environment_parameter("KEY", None)
    # withEnvironmentConnectionString
    container.with_environment_connection_string("KEY", None)
    # withConnectionProperty — with ReferenceExpression
    built_connection_string.with_connection_property("Key", "Value")
    # withConnectionPropertyValue — with string
    built_connection_string.with_connection_property_value("Key", "Value")
    # excludeFromManifest
    container.exclude_from_manifest()
    # excludeFromMcp
    container.exclude_from_mcp()
    # waitForCompletion (pre-existing)
    container.wait_for_completion()
    # withDeveloperCertificateTrust
    container.with_developer_certificate_trust()
    # withCertificateTrustScope
    container.with_certificate_trust_scope()
    # withHttpsDeveloperCertificate
    container.with_https_developer_certificate()
    # withoutHttpsCertificate
    container.without_https_certificate()
    # withChildRelationship
    container.with_child_relationship()
    # withIconName
    container.with_icon_name()
    # withHttpProbe
    container.with_http_probe("liveness")
    # withRemoteImageName
    container.with_remote_image_name()
    # withRemoteImageTag
    container.with_remote_image_tag()
    # withMcpServer
    container.with_mcp_server()
    # withRequiredCommand
    container.with_required_command("docker")
    # withToolIgnoreExistingFeeds
    tool.with_tool_ignore_existing_feeds()
    # withToolIgnoreFailedSources
    tool.with_tool_ignore_failed_sources()
    # withToolPackage
    tool.with_tool_package()
    # withToolPrerelease
    tool.with_tool_prerelease()
    # withToolSource
    tool.with_tool_source()
    # withToolVersion
    tool.with_tool_version()
    # publishAsDockerFile
    tool.publish_as_docker_file()
    # ===================================================================
    container.with_pipeline_step_factory("step", lambda *_args, **_kwargs: None)
    container.with_pipeline_configuration(lambda *_args, **_kwargs: None)
    container.with_pipeline_configuration(lambda *_args, **_kwargs: None)
    # ===================================================================
    _app_host_directory = builder.app_host_dir
    host_environment = builder.env
    _is_development = host_environment.is_development()
    _is_production = host_environment.is_production()
    _is_staging = host_environment.is_staging()
    _is_specific_environment = host_environment.is_environment()
    builder_configuration = builder.get_configuration()
    _config_value = builder_configuration.get_config_value()
    _connection_string = builder_configuration.get_connection_string()
    _config_section = builder_configuration.get_section()
    _config_children = builder_configuration.get_children()
    _config_exists = builder_configuration.exists()
    builder_execution_context = builder.execution_context
    execution_context_service_provider = builder_execution_context.service_provider
    _distributed_application_model_from_execution_context = execution_context_service_provider.get_distributed_application_model()
    before_start_subscription = builder.subscribe_before_start(lambda *_args, **_kwargs: None)
    after_resources_created_subscription = builder.subscribe_after_resources_created(lambda *_args, **_kwargs: None)
    builder_eventing = builder.eventing
    builder_eventing.unsubscribe(None)
    builder_eventing.unsubscribe(None)
    container.on_before_resource_started(lambda *_args, **_kwargs: None)
    container.on_resource_stopped(lambda *_args, **_kwargs: None)
    built_connection_string.on_connection_string_available(lambda *_args, **_kwargs: None)
    container.on_initialize_resource(lambda *_args, **_kwargs: None)
    container.on_resource_endpoints_allocated(lambda *_args, **_kwargs: None)
    container.on_resource_ready(lambda *_args, **_kwargs: None)
    # withEnvironment
    container.with_environment("KEY", "value")
    # withEndpoint
    container.with_endpoint()
    # withHttpEndpoint
    container.with_http_endpoint()
    # withHttpsEndpoint
    container.with_https_endpoint()
    # withExternalHttpEndpoints
    container.with_external_http_endpoints()
    # asHttp2Service
    container.as_http2_service()
    # withArgs
    container.with_args()
    # withParentRelationship
    container.with_parent_relationship()
    # withExplicitStart
    container.with_explicit_start()
    # withUrl
    container.with_url("http://localhost")
    # withUrlExpression
    container.with_url_expression("http://localhost")
    # withHttpHealthCheck
    container.with_http_health_check()
    # withCommand
    container.with_command("command", "Command", lambda *_args, **_kwargs: {"success": True})
    app = builder.build()
    _distributed_app_connection_string = app.get_connection_string()
    _distributed_app_endpoint = app.get_endpoint("default")
    _distributed_app_endpoint_for_network = app.get_endpoint_for_network("resource")
    builder.run()
