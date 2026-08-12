# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addKafka — factory method with optional port
    kafka = builder.add_kafka("resource")
    # withKafkaUI — adds Kafka UI management container with callback
    kafka_with_ui = kafka.with_kafka_ui()
    # withDataVolume — adds a data volume
    kafka_with_ui.with_data_volume()
    # withDataBindMount — adds a data bind mount
    kafka2 = builder.add_kafka("resource")
    kafka2.with_data_bind_mount()
    # ---- Property access on KafkaServerResource ----
    _endpoint = kafka.primary_endpoint
    _host = kafka.host
    _port = kafka.port
    _internal = kafka.internal_endpoint
    _cstr = kafka.connection_string_expression
    builder.run()
