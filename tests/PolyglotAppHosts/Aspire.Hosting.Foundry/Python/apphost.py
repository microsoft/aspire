from aspire_app import create_builder


with create_builder() as builder:
    foundry = builder.add_foundry("foundry")

    chat = foundry.add_deployment(
        "chat",
        "Phi-4",
        model_version="1",
        format="Microsoft")

    model = {
        "name": "gpt-4.1-mini",
        "version": "1",
        "format": "OpenAI"
    }

    _chat_from_model = foundry.add_deployment("chat-from-model", model)

    local_foundry = builder.add_foundry("local-foundry").run_as_foundry_local()
    _local_chat = local_foundry.add_deployment(
        "local-chat",
        "Phi-3.5-mini-instruct",
        model_version="1",
        format="Microsoft")

    registry = builder.add_azure_container_registry("registry")
    key_vault = builder.add_azure_key_vault("vault")
    app_insights = builder.add_azure_application_insights("insights")
    cosmos = builder.add_azure_cosmos_db("cosmos")
    storage = builder.add_azure_storage("storage")
    search = builder.add_azure_search("search")

    project = foundry.add_project("project")
    project.with_role_assignments(foundry, [
        "CognitiveServicesOpenAIContributor",
        "CognitiveServicesOpenAIUser",
        "CognitiveServicesUser",
    ])
    project.with_container_registry(registry)
    project.with_key_vault(key_vault)
    project.with_app_insights(app_insights)

    _cosmos_connection = project.add_cosmos_connection(cosmos)
    _storage_connection = project.add_storage_connection(storage)
    _registry_connection = project.add_container_registry_connection(registry)
    _key_vault_connection = project.add_key_vault_connection(key_vault)
    _search_connection = project.add_search_connection(search)

    # Prompt Agent tools
    code_interpreter = project.add_code_interpreter_tool("code-interpreter")
    file_search = project.add_file_search_tool("file-search", ["vs_placeholder"])
    web_search = project.add_web_search_tool("web-search")
    image_gen = project.add_image_generation_tool("image-gen")
    computer_use = project.add_computer_use_tool("computer-use")
    ai_search_tool = project.add_ai_search_tool("ai-search-tool")
    ai_search_tool.with_reference(search)
    bing_conn = project.add_bing_grounding_connection(
        "bing-conn",
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing")
    bing_tool = project.add_bing_grounding_tool("bing-tool")
    bing_tool.with_reference(bing_conn)
    bing_tool2 = project.add_bing_grounding_tool("bing-tool-2")
    bing_tool2.with_reference("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing")
    bing_param = builder.add_parameter("bing-resource-id")
    bing_tool3 = project.add_bing_grounding_tool("bing-tool-3")
    bing_tool3.with_reference(bing_param)
    sharepoint = project.add_share_point_tool("sharepoint-tool", ["https://contoso.sharepoint.com", "MySite"])
    fabric = project.add_fabric_tool("fabric-tool", ["workspace-id"])
    az_func = project.add_azure_function_tool(
        "az-func-tool",
        "myFunction",
        "Does something",
        "{}",
        "https://queue.core.windows.net",
        "input-q",
        "https://queue.core.windows.net",
        "output-q")
    func_tool = project.add_function_tool("func-tool", "myFunc", "{}")

    # Prompt Agent
    _prompt_agent = project.add_prompt_agent(chat, "prompt-agent")
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

    builder_project_foundry = builder.add_foundry("builder-project-foundry")
    builder_project = builder_project_foundry.add_project("builder-project")
    _builder_project_model = builder_project.add_model_deployment(
        "builder-project-model",
        "Phi-4-mini",
        model_version="1",
        format="Microsoft")
    _project_model = project.add_model_deployment("project-model", model)
    hosted_agent = builder.add_executable(
        "hosted-agent",
        "node",
        ".",
        [
            "-e",
            """
const http = require('node:http');
const port = Number(process.env.DEFAULT_AD_PORT ?? '8088');
const server = http.createServer((req, res) => {
  if (req.url === '/liveness' || req.url === '/readiness') {
    res.writeHead(200, { 'content-type': 'text/plain' });
    res.end('ok');
    return;
  }
  if (req.url === '/responses') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ output: 'hello from validation app host' }));
    return;
  }
  res.writeHead(404);
  res.end();
});
server.listen(port, '127.0.0.1');
"""
        ])

    hosted_agent.publish_as_hosted_agent(project=project)

    api = builder.add_container("api", "nginx")
    foundry.with_role_assignments(registry)

    _deployment_name = chat.deployment_name
    _model_name = chat.model_name
    _format = chat.format
    _version = chat.model_version
    _connection_string = chat.connection_string_expression
    _deployment_parent = chat.parent

    builder.run()
