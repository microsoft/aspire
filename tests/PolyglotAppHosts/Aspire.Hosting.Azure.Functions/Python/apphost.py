# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ── 1. addAzureFunctionsProject (path-based overload) ───────────────────────
    func_app = builder.add_azure_functions_project("resource")
    # ── 2. withHostStorage — specify custom Azure Storage for Functions host ────
    storage = builder.add_azure_storage("resource")
    func_app.with_host_storage()
    # ── 4. withReference from base builder — standard resource references ───────
    another_storage = builder.add_azure_storage("resource")
    func_app.with_reference()
    builder.run()
