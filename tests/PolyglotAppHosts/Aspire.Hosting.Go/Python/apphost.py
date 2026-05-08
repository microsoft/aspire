# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder

with create_builder() as builder:
    # Basic Go app — go run .
    api = builder.add_go_app("api", "../go-api")

    # Go app with build tags and linker flags
    worker = builder.add_go_app("worker", "../go-worker")
    worker.with_build_tags(["netgo", "osusergo"])
    worker.with_ld_flags("-s -w -X main.version=1.0.0")

    # Go app with pre-start lifecycle helpers and debug-friendly compiler flags
    managed = builder.add_go_app("managed", "../go-managed")
    managed.with_tidy()
    managed.with_vendor()
    managed.with_vet()
    managed.with_race_detector()
    managed.with_gc_flags("all=-N -l")
    managed.with_app_args(["--config", "prod.yaml"])

    # Go app with headless Delve server for remote debugging
    debug_service = builder.add_go_app("debug-service", "../go-debug-service")
    debug_service.with_delve_server()

    builder.run()
