import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        var foundry = builder.addFoundry("foundry");

        var chat = foundry
            .addDeployment("chat", "Phi-4", "1", "Microsoft")
            .withProperties((deployment) -> {
                deployment.setDeploymentName("chat-deployment");
                deployment.setSkuCapacity(10);
                var _capacity = deployment.skuCapacity();
            });

        var model = new FoundryModel();
        model.setName("gpt-4.1-mini");
        model.setVersion("1");
        model.setFormat("OpenAI");

        var _chatFromModel = foundry.addDeploymentFromModel("chat-from-model", model);

        var localFoundry = builder.addFoundry("local-foundry")
            .runAsFoundryLocal();

        var _localChat = localFoundry.addDeployment("local-chat", "Phi-3.5-mini-instruct", "1", "Microsoft");

        var registry = builder.addAzureContainerRegistry("registry");
        var keyVault = builder.addAzureKeyVault("vault");
        var appInsights = builder.addAzureApplicationInsights("insights");
        var cosmos = builder.addAzureCosmosDB("cosmos");
        var storage = builder.addAzureStorage("storage");

        var project = foundry.addProject("project");
        project.withContainerRegistry(registry);
        project.withKeyVault(keyVault);
        project.withAppInsights(appInsights);

        var _cosmosConnection = project.addCosmosConnection(cosmos);
        var _storageConnection = project.addStorageConnection(storage);
        var _registryConnection = project.addContainerRegistryConnection(registry);
        var _keyVaultConnection = project.addKeyVaultConnection(keyVault);

        var builderProjectFoundry = builder.addFoundry("builder-project-foundry");
        var builderProject = builderProjectFoundry.addProject("builder-project");
        var _builderProjectModel = builderProject.addModelDeployment("builder-project-model", "Phi-4-mini", "1", "Microsoft");
        var _projectModel = project.addModelDeploymentFromModel("project-model", model);
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
        api.withRoleAssignments(foundry, new FoundryRole[] {
            FoundryRole.COGNITIVE_SERVICES_OPEN_AIUSER,
            FoundryRole.COGNITIVE_SERVICES_USER
        });

        var _deploymentName = chat.deploymentName();
        var _modelName = chat.modelName();
        var _format = chat.format();
        var _version = chat.modelVersion();
        var _connectionString = chat.connectionStringExpression();

        builder.build().run();
    }
