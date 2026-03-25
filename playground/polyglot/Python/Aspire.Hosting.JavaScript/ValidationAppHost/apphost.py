# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    node_app = builder.add_node_app("resource")
    node_app.with_npm()
    node_app.with_bun()
    node_app.with_yarn()
    node_app.with_pnpm()
    node_app.with_build_script()
    node_app.with_run_script()
    java_script_app = builder.add_java_script_app("resource")
    java_script_app.with_environment("KEY", "value")
    vite_app = builder.add_vite_app("resource")
    vite_app.with_vite_config()
    vite_app.with_pnpm()
    vite_app.with_build_script()
    vite_app.with_run_script()
    builder.run()
