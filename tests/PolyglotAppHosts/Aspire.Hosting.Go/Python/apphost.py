# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Basic Go app — go run .
    api = builder.add_go_app("resource")

    # Go app with build tags and linker flags
    worker = builder.add_go_app("resource")
    worker.with_build_tags()
    worker.with_ld_flags()

    # Go app with pre-start lifecycle helpers and debug-friendly compiler flags
    managed = builder.add_go_app("resource")
    managed.with_tidy()
    managed.with_vendor()
    managed.with_vet()
    managed.with_race_detector()
    managed.with_gc_flags()
    managed.with_app_args()

    # Go app with headless Delve server for remote debugging
    debug_service = builder.add_go_app("resource")
    debug_service.with_delve_server()

    builder.run()
