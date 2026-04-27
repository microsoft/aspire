# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    foundry = builder.add_foundry("resource")
    chat = foundry
    model = None
    _chat_from_model = foundry.add_deployment("resource")
    local_foundry = builder.add_foundry("resource")
    _local_chat = local_foundry.add_deployment("resource")
    registry = builder.add_azure_container_registry("resource")
    key_vault = builder.add_azure_key_vault("resource")
    app_insights = builder.add_azure_application_insights("resource")
    cosmos = builder.add_azure_cosmos_db("resource")
    storage = builder.add_azure_storage("resource")
    search = builder.add_azure_search("resource")
    project = foundry.add_project("resource", ".", "default")
    project.with_container_registry()
    project.with_key_vault()
    project.with_app_insights()
    _cosmos_connection = project.add_cosmos_connection("resource")
    _storage_connection = project.add_storage_connection("resource")
    _registry_connection = project.add_container_registry_connection("resource")
    _key_vault_connection = project.add_key_vault_connection("resource")
    _search_connection = project.add_search_connection("resource")

    # Prompt Agent tools
    code_interpreter = project.add_code_interpreter_tool("resource")
    file_search = project.add_file_search_tool("resource")
    web_search = project.add_web_search_tool("resource")
    image_gen = project.add_image_generation_tool("resource")
    computer_use = project.add_computer_use_tool("resource")
    ai_search_tool = project.add_ai_search_tool("resource")
    ai_search_tool.with_reference()
    bing_conn = project.add_bing_grounding_connection("resource", "resource")
    bing_tool = project.add_bing_grounding_tool("resource")
    bing_tool.with_reference(bing_conn)
    bing_tool2 = project.add_bing_grounding_tool("resource2")
    bing_tool2.with_reference("resource")
    bing_param = builder.add_parameter("resource")
    bing_tool3 = project.add_bing_grounding_tool("resource3")
    bing_tool3.with_reference(bing_param)
    sharepoint = project.add_share_point_tool("resource")
    fabric = project.add_fabric_tool("resource")
    az_func = project.add_azure_function_tool("resource")
    func_tool = project.add_function_tool("resource")

    # Prompt Agent
    _prompt_agent = project.add_prompt_agent("resource")
    _prompt_agent.with_tool(code_interpreter)
    _prompt_agent.with_tool(file_search)
    _prompt_agent.with_tool(web_search)
    _prompt_agent.with_tool(image_gen)
    _prompt_agent.with_tool(computer_use)
    _prompt_agent.with_tool(ai_search_tool)
    _prompt_agent.with_tool(bing_tool)
    _prompt_agent.with_tool(sharepoint)
    _prompt_agent.with_tool(fabric)
    _prompt_agent.with_tool(az_func)
    _prompt_agent.with_tool(func_tool)

    builder_project_foundry = builder.add_foundry("resource")
    builder_project = builder_project_foundry.add_project("resource", ".", "default")
    _builder_project_model = builder_project.add_model_deployment("resource")
    _project_model = project.add_model_deployment("resource")
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
