# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    rabbitmq = builder.add_rabbit_mq("resource")
    rabbitmq.with_data_volume()
    rabbitmq.with_management_plugin()
    rabbitmq2 = builder
    # ---- Property access on RabbitMQServerResource ----
    _endpoint = rabbitmq.primary_endpoint
    _mgmt_endpoint = rabbitmq.management_endpoint
    _host = rabbitmq.host
    _port = rabbitmq.port
    _uri = rabbitmq.uri_expression
    _user_name = rabbitmq.user_name_reference
    _cstr = rabbitmq.connection_string_expression
    builder.run()
