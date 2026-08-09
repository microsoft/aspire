# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    signalr = builder.add_azure_signal_r("resource")
    signalr.run_as_emulator()
    builder.run()
