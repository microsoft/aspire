# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    event_hubs = builder.add_azure_event_hubs("resource")
    event_hubs.with_event_hubs_role_assignments()
    hub = event_hubs.add_hub("resource")
    hub.with_properties()
    consumer_group = hub.add_consumer_group("resource")
    consumer_group.with_event_hubs_role_assignments()
    event_hubs.run_as_emulator()
    builder.run()
