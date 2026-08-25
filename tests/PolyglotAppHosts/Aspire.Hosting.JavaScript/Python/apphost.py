# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    node_app = builder.add_node_app("resource", ".", "app.js")
    node_app.with_npm()
    node_app.with_bun()
    node_app.with_yarn()
    node_app.with_pnpm()
    node_app.with_build_script("build")
    node_app.with_run_script("start")
    _ = node_app.name
    _ = node_app.command
    _ = node_app.working_dir
    java_script_app = builder.add_java_script_app("resource", ".")
    java_script_app.with_env("KEY", "value")
    _ = java_script_app.name
    _ = java_script_app.command
    _ = java_script_app.working_dir
    vite_app = builder.add_vite_app("resource", ".")
    vite_app.with_vite_config("vite.config.js")
    vite_app.with_pnpm()
    vite_app.with_build_script("build")
    vite_app.with_run_script("dev")
    _ = vite_app.name
    _ = vite_app.command
    _ = vite_app.working_dir
    builder.run()
