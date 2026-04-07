// Aspire TypeScript AppHost
// For more information, see: https://aspire.dev

import { AzureOpenAIRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const openai = await builder.addAzureOpenAI('openai');
await openai.addDeployment('chat', 'gpt-4o-mini', '2024-07-18');

const api = await builder.addContainer('api', 'redis:latest');
await api.withCognitiveServicesRoleAssignments(openai, [AzureOpenAIRole.CognitiveServicesOpenAIUser]);

await builder.build().run();
