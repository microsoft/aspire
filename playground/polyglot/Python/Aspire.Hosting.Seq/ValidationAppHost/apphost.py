# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    admin_password = builder.add_parameter("parameter")
    seq = builder.add_seq("resource")
    seq.with_data_volume()
    seq.with_data_volume()
    seq.with_data_bind_mount()
    # ---- Property access on SeqResource ----
    _endpoint = seq.primary_endpoint
    _host = seq.host
    _port = seq.port
    _uri = seq.uri_expression
    _cstr = seq.connection_string_expression
    builder.run()
