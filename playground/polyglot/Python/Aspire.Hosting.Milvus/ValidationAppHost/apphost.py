# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ── 1. addMilvus: basic Milvus server resource ─────────────────────────────
    milvus = builder.add_milvus("resource")
    # ── 2. addMilvus: with custom apiKey parameter ─────────────────────────────
    custom_key = builder.add_parameter("parameter")
    milvus2 = builder.add_milvus("resource")
    # ── 3. addMilvus: with explicit gRPC port ──────────────────────────────────
    builder.add_milvus("resource")
    # ── 4. addDatabase: add database to Milvus server ──────────────────────────
    db = milvus.add_database("resource")
    # ── 5. addDatabase: with custom database name ──────────────────────────────
    milvus.add_database("resource")
    # ── 6. withAttu: add Attu administration tool ──────────────────────────────
    milvus.with_attu()
    # ── 7. withAttu: with container name ────────────────────────────────────────
    milvus2.with_attu()
    # ── 8. withAttu: with configureContainer callback ──────────────────────────
    builder.add_milvus("resource")
    # ── 9. withDataVolume: persistent data volume ──────────────────────────────
    milvus.with_data_volume()
    # ── 10. withDataVolume: with custom name ────────────────────────────────────
    milvus2.with_data_volume()
    # ── 11. withDataBindMount: bind mount for data ─────────────────────────────
    builder.add_milvus("resource")
    # ── 12. withDataBindMount: with read-only flag ─────────────────────────────
    builder.add_milvus("resource")
    # ── 13. withConfigurationFile: custom milvus.yaml ──────────────────────────
    builder.add_milvus("resource")
    # ── 14. Fluent chaining: multiple With* methods ────────────────────────────
    builder.add_milvus("resource")
    # ── 15. withReference: use Milvus database from a container resource ───────
    api = builder.add_container("resource", "image")
    api.with_reference()
    # ── 16. withReference: use Milvus server directly ──────────────────────────
    api.with_reference()
    # ---- Property access on MilvusServerResource ----
    _endpoint = milvus.primary_endpoint
    _host = milvus.host
    _port = milvus.port
    _token = milvus.token
    _uri = milvus.uri_expression
    _cstr = milvus.connection_string_expression
    _databases = None
    builder.run()
