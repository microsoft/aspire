// Aspire TypeScript AppHost
// For more information, see: https://aspire.dev

import { AzureOpenAIRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const openai = await builder.addAzureOpenAI('openai');
const chat = await openai.addDeployment('chat', 'gpt-4o-mini', '2024-07-18');

const api = await builder.addContainer('api', 'redis:latest');
await api.withRoleAssignments(openai, [AzureOpenAIRole.CognitiveServicesOpenAIUser]);

const _deploymentParent = await chat.parent();
const _deploymentConnectionString = await chat.connectionStringExpression();

await builder.build().run();
