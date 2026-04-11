import { type FoundryModel, FoundryRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const foundry = await builder.addFoundry('foundry');

const chat = await foundry
    .addDeployment('chat', 'Phi-4', '1', 'Microsoft')
    .withProperties(async (deployment) => {
        await deployment.deploymentName.set('chat-deployment');
        await deployment.skuCapacity.set(10);
        const _capacity: number = await deployment.skuCapacity.get();
    });

const model: FoundryModel = {
    name: 'gpt-4.1-mini',
    version: '1',
    format: 'OpenAI'
};

const _chatFromModel = await foundry.addDeploymentFromModel('chat-from-model', model);

const localFoundry = await builder.addFoundry('local-foundry')
    .runAsFoundryLocal();

const _localChat = await localFoundry.addDeployment('local-chat', 'Phi-3.5-mini-instruct', '1', 'Microsoft');

const registry = await builder.addAzureContainerRegistry('registry');
const keyVault = await builder.addAzureKeyVault('vault');
const appInsights = await builder.addAzureApplicationInsights('insights');
const cosmos = await builder.addAzureCosmosDB('cosmos');
const storage = await builder.addAzureStorage('storage');
const search = await builder.addAzureSearch('search');

const project = await foundry.addProject('project');
await project.withContainerRegistry(registry);
await project.withKeyVault(keyVault);
await project.withAppInsights(appInsights);
await project.addCapabilityHost('cap-host');
await project.withCapabilityHost(cosmos);
await project.withCapabilityHost(storage);
await project.withCapabilityHost(search);
await project.withCapabilityHost(foundry);

const _cosmosConnection = await project.addCosmosConnection(cosmos);
const _storageConnection = await project.addStorageConnection(storage);
const _registryConnection = await project.addContainerRegistryConnection(registry);
const _keyVaultConnection = await project.addKeyVaultConnection(keyVault);

const builderProjectFoundry = await builder.addFoundry('builder-project-foundry');
const builderProject = await builderProjectFoundry.addProject('builder-project');
const _builderProjectModel = await builderProject.addModelDeployment('builder-project-model', 'Phi-4-mini', '1', 'Microsoft');
const _projectModel = await project.addModelDeploymentFromModel('project-model', model);
const hostedAgent = await builder.addExecutable(
    'hosted-agent',
    'node',
    '.',
    [
        '-e',
        `
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
`
    ]);

await hostedAgent.publishAsHostedAgent({
    project,
    configure: async (configuration) => {
        await configuration.description.set('Validation hosted agent');
        await configuration.cpu.set(1);
        await configuration.memory.set(2);
        await configuration.metadata.set('scenario', 'validation');
        await configuration.environmentVariables.set('VALIDATION_MODE', 'true');
    }
});

const api = await builder.addContainer('api', 'nginx');
await api.withRoleAssignments(foundry, [
    FoundryRole.CognitiveServicesOpenAIUser,
    FoundryRole.CognitiveServicesUser
]);

const _deploymentName = await chat.deploymentName.get();
const _modelName = await chat.modelName.get();
const _format = await chat.format.get();
const _version = await chat.modelVersion.get();
const _connectionString = await chat.connectionStringExpression.get();
const _deploymentParent = await chat.parent.get();

await builder.build().run();
