# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    api_key = builder.add_parameter("parameter")
    openai = builder.add_open_ai("resource")
    openai.add_model("resource")
    builder.run()
