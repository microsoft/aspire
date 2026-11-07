# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    storage = builder.add_azure_storage("funcstorage")

    # ── 1. addDotnetAzureFunction and its typed fluent APIs ─────────────────────
    functions = builder.add_dotnet_azure_function(
        "dotnetfunc",
        "../MyFunctions/MyFunctions.csproj",
    )
    functions.with_host_storage(storage)
    functions.with_build_env("BUILD_KEY", "build-value")
    functions.with_replicas(2)
    functions.disable_forwarded_headers()
    functions.with_endpoints_in_env(["http"])

    # ── 2. Legacy addAzureFunctionsProject compatibility surface ────────────────
    legacy_func_app = builder.add_azure_functions_project(
        "legacyfunc",
        "../OtherFunc/OtherFunc.csproj",
    )
    legacy_func_app.with_host_storage(storage)

    builder.run()
