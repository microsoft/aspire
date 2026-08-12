# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ── 1. addAzureServiceBus ──────────────────────────────────────────────────
    service_bus = builder.add_azure_service_bus("resource")
    # ── 2. runAsEmulator — with configureContainer callback ────────────────────
    emulator_bus = builder
    # ── 3. addServiceBusQueue — factory method returns Queue type ──────────────
    queue = service_bus.add_service_bus_queue("resource")
    # ── 4. addServiceBusTopic — factory method returns Topic type ──────────────
    topic = service_bus.add_service_bus_topic("resource")
    # ── 5. addServiceBusSubscription — factory on Topic returns Subscription ───
    subscription = topic.add_service_bus_subscription("resource")
    # ── DTO types ───────────────────────────────────────────────────────────────
    filter = None
    rule = None
    # TimeSpan properties map to number (ticks) in TypeScript
    queue.with_properties()
    topic.with_properties()
    subscription.with_properties()
    # On the parent ServiceBus resource (all 3 roles)
    service_bus.with_service_bus_role_assignments()
    # On child resources
    queue.with_service_bus_role_assignments()
    topic.with_service_bus_role_assignments()
    subscription.with_service_bus_role_assignments()
    # ── 8. Verify enum values are accessible ────────────────────────────────────
    _sql_filter = None
    _correlation_filter = None
    builder.run()
