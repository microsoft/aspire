# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    foundry = builder.add_foundry("resource")
    chat = foundry
    model = None
    _chat_from_model = foundry.add_deployment_from_model("resource")
    local_foundry = builder.add_foundry("resource")
    _local_chat = local_foundry.add_deployment("resource")
    registry = builder.add_azure_container_registry("resource")
    key_vault = builder.add_azure_key_vault("resource")
    app_insights = builder.add_azure_application_insights("resource")
    cosmos = builder.add_azure_cosmos_db("resource")
    storage = builder.add_azure_storage("resource")
    project = foundry.add_project("resource", ".", "default")
    project.with_container_registry()
    project.with_key_vault()
    project.with_app_insights()
    _cosmos_connection = project.add_cosmos_connection("resource")
    _storage_connection = project.add_storage_connection("resource")
    _registry_connection = project.add_container_registry_connection("resource")
    _key_vault_connection = project.add_key_vault_connection("resource")
    builder_project_foundry = builder.add_foundry("resource")
    builder_project = builder_project_foundry.add_project("resource", ".", "default")
    _builder_project_model = builder_project.add_model_deployment("resource")
    project_model = project.add_model_deployment_from_model("resource")
    _prompt_agent = project.add_and_publish_prompt_agent("resource")
    hosted_agent = builder.add_executable("resource", "echo", ".", [])
    http = None
    port = None
    server = http.create_server()
    res.write_head()
    res.end()
    server.listen()
    hosted_agent.publish_as_hosted_agent()
    api = builder.add_container("resource", "image")
    api.with_role_assignments()
    _deployment_name = chat.deployment_name
    _model_name = chat.model_name
    _format = chat.format
    _version = chat.model_version
    _connection_string = chat.connection_string_expression
    builder.run()
