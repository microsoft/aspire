import { AzureContainerRegistryRole, FoundryModels, FoundryRole, type FoundryModel, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const foundry = await builder.addFoundry('foundry');

const chat = await foundry
    .addDeployment('chat', 'Phi-4', { modelVersion: '1', format: 'Microsoft' })
    .withProperties(async (deployment) => {
        await deployment.deploymentName.set('chat-deployment');
        await deployment.skuCapacity.set(10);
        const _capacity: number = await deployment.skuCapacity.get();
    });

const model: FoundryModel = FoundryModels.OpenAI.Gpt41Mini;

const _chatFromModel = await foundry.addDeployment('chat-from-model', model);

const localFoundry = await builder.addFoundry('local-foundry')
    .runAsFoundryLocal();

const _localChat = await localFoundry.addDeployment('local-chat', 'Phi-3.5-mini-instruct', { modelVersion: '1', format: 'Microsoft' });

const registry = await builder.addAzureContainerRegistry('registry');
const keyVault = await builder.addAzureKeyVault('vault');
const appInsights = await builder.addAzureApplicationInsights('insights');
const cosmos = await builder.addAzureCosmosDB('cosmos');
const storage = await builder.addAzureStorage('storage');
const search = await builder.addAzureSearch('search');

const project = await foundry.addProject('project');
await project.withRoleAssignments(foundry, [
    FoundryRole.CognitiveServicesOpenAIContributor,
    FoundryRole.CognitiveServicesOpenAIUser,
    FoundryRole.CognitiveServicesUser
]);
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
const _searchConnection = await project.addSearchConnection(search);

// Prompt Agent tools
const codeInterpreter = await project.addCodeInterpreterTool('code-interpreter');
const fileSearch = await project.addFileSearchTool('file-search', ['vs_placeholder']);
const webSearch = await project.addWebSearchTool('web-search');
const imageGen = await project.addImageGenerationTool('image-gen');
const computerUse = await project.addComputerUseTool('computer-use');
const aiSearchTool = await project.addAISearchTool('ai-search-tool');
await aiSearchTool.withReference(search);
const bingConn = await project.addBingGroundingConnection('bing-conn', '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing');
const bingTool = await project.addBingGroundingTool('bing-tool');
await bingTool.withReference(bingConn);
const bingTool2 = await project.addBingGroundingTool('bing-tool-2');
await bingTool2.withReference('/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing');
const bingParam = await builder.addParameter('bing-resource-id');
const bingTool3 = await project.addBingGroundingTool('bing-tool-3');
await bingTool3.withReference(bingParam);
const sharepoint = await project.addSharePointTool('sharepoint-tool', ['https://contoso.sharepoint.com', 'MySite']);
const fabric = await project.addFabricTool('fabric-tool', ['workspace-id']);
const azFunc = await project.addAzureFunctionTool('az-func-tool', 'myFunction', 'Does something', '{}', 'https://queue.core.windows.net', 'input-q', 'https://queue.core.windows.net', 'output-q');
const funcTool = await project.addFunctionTool('func-tool', 'myFunc', '{}');

// Prompt Agent
const _promptAgent = await project.addPromptAgent(chat, 'prompt-agent');
await _promptAgent.withTool(codeInterpreter);
await _promptAgent.withTool(fileSearch);
await _promptAgent.withTool(webSearch);
await _promptAgent.withTool(imageGen);
await _promptAgent.withTool(computerUse);
await _promptAgent.withTool(aiSearchTool);
await _promptAgent.withTool(bingTool);
await _promptAgent.withTool(sharepoint);
await _promptAgent.withTool(fabric);
await _promptAgent.withTool(azFunc);
await _promptAgent.withTool(funcTool);

const builderProjectFoundry = await builder.addFoundry('builder-project-foundry');
const builderProject = await builderProjectFoundry.addProject('builder-project');
const _builderProjectModel = await builderProject.addModelDeployment('builder-project-model', 'Phi-4-mini', { modelVersion: '1', format: 'Microsoft' });
const _projectModel = await project.addModelDeployment('project-model', FoundryModels.Microsoft.Phi4);
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
await foundry.withRoleAssignments(registry, [AzureContainerRegistryRole.AcrPull]);

const _deploymentName = await chat.deploymentName.get();
const _modelName = await chat.modelName.get();
const _format = await chat.format.get();
const _version = await chat.modelVersion.get();
const _connectionString = await chat.connectionStringExpression();
const _deploymentParent = await chat.parent.get();

await builder.build().run();
