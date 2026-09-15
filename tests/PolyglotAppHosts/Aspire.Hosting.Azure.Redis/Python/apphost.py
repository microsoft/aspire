# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_managed_redis(infrastructure: AzureResourceInfrastructure) -> None:
    cluster = infrastructure.get_redis_enterprise_cluster()
    cluster.minimum_tls_version = "Tls1_2"
    _minimum_tls_version = cluster.minimum_tls_version


def create_legacy_redis(infrastructure: AzureResourceInfrastructure) -> None:
    # The obsolete Azure Redis hosting API is not exported.
    redis = infrastructure.add_redis_resource("legacyRedis")
    sku = infrastructure.create_redis_sku()
    sku.name = "Basic"
    sku.family = "BasicOrStandard"
    sku.capacity = 0
    redis.sku = sku


def configure_legacy_redis(infrastructure: AzureResourceInfrastructure) -> None:
    redis = infrastructure.get_redis_resource()
    redis.enable_non_ssl_port = False
    _enable_non_ssl_port = redis.enable_non_ssl_port


with create_builder() as builder:
    key_vault = builder.add_azure_key_vault("resource")
    cache = builder.add_azure_managed_redis("resource")
    cache.configure_infrastructure(configure_managed_redis)
    legacy_cache = builder.add_azure_infrastructure("legacyRedis", create_legacy_redis)
    legacy_cache.configure_infrastructure(configure_legacy_redis)
    access_key_cache = builder.add_azure_managed_redis("resource")
    container_cache = builder.add_azure_managed_redis("resource")
    access_key_cache.with_access_key_authentication()
    access_key_cache.with_access_key_authentication(key_vault_builder=key_vault)
    container_cache.run_as_container()
    _connection_string = cache.connection_string_expression
    _host_name = cache.host_name
    _port = cache.port
    _uri = cache.uri_expression
    _use_access_key_authentication = cache.use_access_key_authentication
    _access_key_connection_string = access_key_cache.connection_string_expression
    _access_key_host_name = access_key_cache.host_name
    _access_key_password = access_key_cache.password
    _access_key_uri = access_key_cache.uri_expression
    _uses_access_key_authentication = access_key_cache.use_access_key_authentication
    _container_connection_string = container_cache.connection_string_expression
    _container_host_name = container_cache.host_name
    _container_port = container_cache.port
    _container_password = container_cache.password
    _container_uri = container_cache.uri_expression
    builder.run()
