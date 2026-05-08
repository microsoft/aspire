# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder

with create_builder() as builder:
    # Basic Go app — go run .
    builder.add_go_app("api", "../go-api")

    # Go app with build tags and linker flags via keyword args
    builder.add_go_app("worker", "../go-worker",
                       build_tags=["netgo", "osusergo"],
                       ld_flags="-s -w -X main.version=1.0.0")

    # Go app with pre-start lifecycle helpers and all build options
    managed = builder.add_go_app("managed", "../go-managed",
                                 build_tags=["integration"],
                                 ld_flags="-s -w",
                                 gc_flags="all=-N -l",
                                 race_detector=True)
    managed.with_mod_tidy()
    managed.with_mod_vendor()
    managed.with_mod_download()
    managed.with_vet_tool()
    managed.with_app_args(["--config", "prod.yaml"])

    # Go app with headless Delve server for remote debugging
    debug_service = builder.add_go_app("debug-service", "../go-debug-service")
    debug_service.with_delve_server()

    builder.run()
