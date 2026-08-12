# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # For more information, see: https://aspire.dev
    openai = builder.add_azure_open_ai("resource")
    openai.add_deployment("resource")
    api = builder.add_container("resource", "image")
    api.with_cognitive_services_role_assignments()
    builder.run()
