# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addNats — factory method with default options
    nats = builder.add_nats("resource")
    # withJetStream — enable JetStream support
    nats.with_jet_stream()
    # withDataVolume — add persistent data volume
    nats.with_data_volume()
    # withDataVolume — with custom name and readOnly option
    nats2 = builder.add_nats("resource")
    # withDataBindMount — bind mount a host directory
    nats3 = builder.add_nats("resource")
    nats3.with_data_bind_mount()
    # addNats — with custom userName and password parameters
    custom_user = builder.add_parameter("parameter")
    custom_pass = builder.add_parameter("parameter")
    nats4 = builder.add_nats("resource")
    # withReference — a container referencing a NATS resource (connection string)
    consumer = builder.add_container("resource", "image")
    consumer.with_reference()
    # withReference — with explicit connection name option
    consumer.with_reference()
    # ---- Property access on NatsServerResource ----
    _endpoint = nats.primary_endpoint
    _host = nats.host
    _port = nats.port
    _uri = nats.uri_expression
    _user_name = nats.user_name_reference
    _cstr = nats.connection_string_expression
    builder.run()
