# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import FoundryModels, create_builder


with create_builder() as builder:
    foundry = builder.add_foundry("foundry")
    chat = foundry.add_deployment("chat", "Phi-4", model_version="1", format="Microsoft")
    model = FoundryModels.OpenAI.Gpt41Mini
    _chat_from_model = foundry.add_deployment("chat-from-model", model)
    local_foundry = builder.add_foundry("local-foundry")
    _local_chat = local_foundry.add_deployment("local-chat", "Phi-4", model_version="1", format="Microsoft")
    registry = builder.add_azure_container_registry("resource")
    key_vault = builder.add_azure_key_vault("resource")
    app_insights = builder.add_azure_application_insights("resource")
    cosmos = builder.add_azure_cosmos_db("resource")
    storage = builder.add_azure_storage("resource")
    project = foundry.add_project("project", ".", "default")
    project.with_container_registry(registry)
    project.with_key_vault(key_vault)
    project.with_app_insights(app_insights)
    _cosmos_connection = project.add_cosmos_connection(cosmos)
    _storage_connection = project.add_storage_connection(storage)
    _registry_connection = project.add_container_registry_connection(registry)
    _key_vault_connection = project.add_key_vault_connection(key_vault)
    builder_project_foundry = builder.add_foundry("builder-project-foundry")
    builder_project = builder_project_foundry.add_project("builder-project", ".", "default")
    _builder_project_model = builder_project.add_model_deployment("builder-project-model", "Phi-4-mini", model_version="1", format="Microsoft")
    _project_model = project.add_model_deployment("project-model", FoundryModels.Microsoft.Phi4)
    hosted_agent = builder.add_executable("hosted-agent", "echo", ".", [])
    hosted_agent.publish_as_hosted_agent()
    api = builder.add_container("api", "nginx")
    api.with_role_assignments(foundry)
    _deployment_name = chat.deployment_name
    _model_name = chat.model_name
    _format = chat.format
    _version = chat.model_version
    _connection_string = chat.connection_string_expression
    builder.run()
