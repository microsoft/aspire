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

        var project = foundry.addProject("project");
        project.withContainerRegistry(registry);
        project.withKeyVault(keyVault);
        project.withAppInsights(appInsights);

        var _cosmosConnection = project.addConnection(cosmos);
        var _storageConnection = project.addConnection(storage);
        var _registryConnection = project.addConnection(registry);
        var _keyVaultConnection = project.addConnection(keyVault);

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
                configuration.setMetadata(null);
                configuration.setEnvironmentVariables(null);
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
