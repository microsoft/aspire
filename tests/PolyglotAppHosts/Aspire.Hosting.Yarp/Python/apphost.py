# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    build_version = builder.add_parameter_from_config("parameter", "Config:Key")
    build_secret = builder.add_parameter_from_config("parameter", "Config:Key")
    static_files_source = builder.add_container("resource", "image")
    backend = builder.add_container("resource", "image")
    external_backend = builder.add_external_service("resource")
    proxy = builder.add_yarp("resource")
    proxy.with_volume()
    proxy.with_build_arg()
    proxy.with_build_secret()
    proxy.with_configuration()
    proxy.publish_as_connection_string()
    builder.run()
