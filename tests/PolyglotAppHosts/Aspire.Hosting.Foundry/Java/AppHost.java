import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        var foundry = builder.addFoundry("foundry");

        var chat = foundry
            .addDeployment("chat", "Phi-4", new AddDeploymentOptions().modelVersion("1").format("Microsoft"))
            .withProperties((deployment) -> {
                deployment.setDeploymentName("chat-deployment");
                deployment.setSkuCapacity(10);
                var _capacity = deployment.skuCapacity();
            });

        var model = FoundryModels.OpenAI.Gpt41Mini;

        var _chatFromModel = foundry.addDeployment("chat-from-model", model);

        var localFoundry = builder.addFoundry("local-foundry")
            .runAsFoundryLocal();

        var _localChat = localFoundry.addDeployment("local-chat", "Phi-3.5-mini-instruct", new AddDeploymentOptions().modelVersion("1").format("Microsoft"));

        var registry = builder.addAzureContainerRegistry("registry");
        var keyVault = builder.addAzureKeyVault("vault");
        var appInsights = builder.addAzureApplicationInsights("insights");
        var cosmos = builder.addAzureCosmosDB("cosmos");
        var storage = builder.addAzureStorage("storage");
        var search = builder.addAzureSearch("search");

        var project = foundry.addProject("project");
        project.withContainerRegistry(registry);
        project.withKeyVault(keyVault);
        project.withAppInsights(appInsights);

        var _cosmosConnection = project.addCosmosConnection(cosmos);
        var _storageConnection = project.addStorageConnection(storage);
        var _registryConnection = project.addContainerRegistryConnection(registry);
        var _keyVaultConnection = project.addKeyVaultConnection(keyVault);
        var _searchConnection = project.addSearchConnection(search);

        // Prompt Agent tools
        var codeInterpreter = project.addCodeInterpreterTool("code-interpreter");
        var fileSearch = project.addFileSearchTool("file-search", new String[] { "vs_placeholder" });
        var webSearch = project.addWebSearchTool("web-search");
        var imageGen = project.addImageGenerationTool("image-gen");
        var computerUse = project.addComputerUseTool("computer-use");
        var aiSearchTool = project.addAISearchTool("ai-search-tool");
        aiSearchTool.withReference(search);
        var bingConn = project.addBingGroundingConnection("bing-conn", "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing");
        var bingTool = project.addBingGroundingTool("bing-tool");
        bingTool.withReference(bingConn);
        var bingTool2 = project.addBingGroundingTool("bing-tool-2");
        bingTool2.withReference("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing");
        var bingParam = builder.addParameter("bing-resource-id");
        var bingTool3 = project.addBingGroundingTool("bing-tool-3");
        bingTool3.withReference(bingParam);
        var sharepoint = project.addSharePointTool("sharepoint-tool", new String[] { "https://contoso.sharepoint.com", "MySite" });
        var fabric = project.addFabricTool("fabric-tool", new String[] { "workspace-id" });
        var azFunc = project.addAzureFunctionTool("az-func-tool", "myFunction", "Does something", "{}", "https://queue.core.windows.net", "input-q", "https://queue.core.windows.net", "output-q");
        var funcTool = project.addFunctionTool("func-tool", "myFunc", "{}");

        // Prompt Agent
        var _promptAgent = project.addPromptAgent(chat, "prompt-agent");
        _promptAgent.withTool(codeInterpreter);
        _promptAgent.withTool(fileSearch);
        _promptAgent.withTool(webSearch);
        _promptAgent.withTool(imageGen);
        _promptAgent.withTool(computerUse);
        _promptAgent.withTool(aiSearchTool);
        _promptAgent.withTool(bingTool);
        _promptAgent.withTool(sharepoint);
        _promptAgent.withTool(fabric);
        _promptAgent.withTool(azFunc);
        _promptAgent.withTool(funcTool);

        var builderProjectFoundry = builder.addFoundry("builder-project-foundry");
        var builderProject = builderProjectFoundry.addProject("builder-project");
        var _builderProjectModel = builderProject.addModelDeployment("builder-project-model", "Phi-4-mini", new AddModelDeploymentOptions().modelVersion("1").format("Microsoft"));
        var _projectModel = project.addModelDeployment("project-model", FoundryModels.Microsoft.Phi4);
        var hostedAgent = builder.addExecutable(
            "hosted-agent",
            "node",
            ".",
            new String[] {
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
            });

        hostedAgent.publishAsHostedAgent(new PublishAsHostedAgentOptions()
            .project(project)
            .configure((configuration) -> {
                configuration.setDescription("Validation hosted agent");
                configuration.setCpu(1);
                configuration.setMemory(2);
                configuration.metadata().put("scenario", "validation");
                configuration.environmentVariables().put("VALIDATION_MODE", "true");
            }));

        var api = builder.addContainer("api", "nginx");
        foundry.withRoleAssignments(registry, new AzureContainerRegistryRole[] {
            AzureContainerRegistryRole.ACR_PULL
        });

        var _deploymentName = chat.deploymentName();
        var _modelName = chat.modelName();
        var _format = chat.format();
        var _version = chat.modelVersion();
        var _connectionString = chat.connectionStringExpression();

        builder.build().run();
    }
