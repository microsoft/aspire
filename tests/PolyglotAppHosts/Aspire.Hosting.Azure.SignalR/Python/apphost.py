# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    service = infrastructure.get_signal_r_service()
    service.disable_local_auth = True
    _disable_local_auth = service.disable_local_auth


with create_builder() as builder:
    signalr = builder.add_azure_signal_r("resource")
    signalr.configure_infrastructure(configure_provisioning)
    signalr.run_as_emulator()
    builder.run()
