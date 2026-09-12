# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, create_builder


def configure_provisioning(infrastructure: AzureResourceInfrastructure) -> None:
    account = infrastructure.get_cognitive_services_account()
    account.tags.set("provisioning-proxy", "python")


with create_builder() as builder:
    # For more information, see: https://aspire.dev
    openai = builder.add_azure_open_ai("resource")
    openai.configure_infrastructure(configure_provisioning)
    openai.add_deployment("resource", "gpt-4o", "2024-05-13")
    api = builder.add_container("resource", "image")
    api.with_cognitive_services_role_assignments(openai, ["CognitiveServicesOpenAIUser"])
    builder.run()
