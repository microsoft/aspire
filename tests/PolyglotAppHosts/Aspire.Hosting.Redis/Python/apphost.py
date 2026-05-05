# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addRedis — full overload with port and password parameter
    password = builder.add_parameter("parameter")
    cache = builder.add_redis("resource")
    # addRedis — overload with explicit port
    cache2 = builder.add_redis("resource", 6380)
    # withDataVolume + withPersistence — fluent chaining on RedisResource
    cache.with_data_volume()
    cache.with_persistence()
    # withDataBindMount on RedisResource
    cache2.with_data_bind_mount()
    # withHostPort on RedisResource
    cache.with_host_port()
    # withPassword on RedisResource
    new_password = builder.add_parameter("parameter")
    cache2.with_password()
    # withRedisCommander — with configureContainer callback exercising withHostPort
    cache.with_redis_commander()
    # withRedisInsight — with configureContainer callback exercising withHostPort, withDataVolume, withDataBindMount
    cache.with_redis_insight()
    # ---- Property access on RedisResource (ExposeProperties = true) ----
    redis = cache
    _endpoint = redis.primary_endpoint
    _host = redis.host
    _port = redis.port
    _tls_enabled = redis.tls_enabled
    _uri = redis.uri_expression
    _cstr = redis.connection_string_expression
    builder.run()
