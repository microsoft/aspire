# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import DistributedApplicationBuilder, create_builder


def add_deno_app(builder: DistributedApplicationBuilder):
    deno_app = builder.add_deno_app("deno-app", "./deno-app", "main.ts")
    deno_app.with_deno(install=False, install_args=["--cached-only"])
    deno_app.with_deno_allow_all(enabled=False)
    deno_app.with_deno_allow("Net", ["localhost:8000"])
    deno_app.with_deno_deny("Read", ["./secrets"])
    deno_app.with_deno_config("./deno.json")
    deno_app.with_deno_import_map("./import_map.json")
    deno_app.with_deno_lock("./deno.lock")
    deno_app.with_deno_no_lock()
    deno_app.with_deno_node_modules_dir(mode="Auto")
    deno_app.with_deno_unstable(["kv", "worker-options"])
    deno_app.with_deno_watch(hmr=True)
    deno_app.with_deno_inspect(mode="InspectWait", host_port="127.0.0.1:9229")
    deno_app.with_deno_run()
    deno_app.with_deno_task("dev")
    deno_app.with_deno_serve()
    deno_app.with_deno_script_args(["--port", "8000"])
    deno_app.with_deno_runtime_args(["--quiet"])


def main():
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
        add_deno_app(builder)
        builder.run()


if __name__ == "__main__":
    main()
