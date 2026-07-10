// Triggers a real Foundry data-plane call against the configured toolbox.
// Verifies that bearer auth and the dev-tunnel URL round-trip correctly by
// creating a one-shot agent whose only tool is the deployed toolbox.
//
// Run: node scripts/invoke-toolbox.mjs
//   PROJECT_ENDPOINT  https://<account>.services.ai.azure.com/api/projects/<project>
//   TOOLBOX_NAME      field-tools (matches the Aspire resource name)
//   TOOLBOX_VERSION   optional; if omitted, the latest version is used
//   MODEL             chat (the model deployment configured in the AppHost)
//   PROMPT            override the user prompt; defaults to a directory lookup

import { AgentsClient } from '@azure/ai-agents';
import { DefaultAzureCredential } from '@azure/identity';

const projectEndpoint = process.env.PROJECT_ENDPOINT;
if (!projectEndpoint) {
    console.error('PROJECT_ENDPOINT is required.');
    process.exit(1);
}

const toolboxName = process.env.TOOLBOX_NAME ?? 'field-tools';
const toolboxVersion = process.env.TOOLBOX_VERSION;
const modelName = process.env.MODEL ?? 'chat';
const prompt = process.env.PROMPT ?? 'Use the lookup_employee tool to look up Ada Lovelace and tell me their role and department.';

const client = new AgentsClient(projectEndpoint, new DefaultAzureCredential());

const toolboxRef = toolboxVersion
    ? `${projectEndpoint.replace(/\/+$/, '')}/toolboxes/${toolboxName}/versions/${toolboxVersion}/mcp?api-version=v1`
    : `${projectEndpoint.replace(/\/+$/, '')}/toolboxes/${toolboxName}/mcp?api-version=v1`;

console.log('Project endpoint :', projectEndpoint);
console.log('Toolbox MCP URL  :', toolboxRef);
console.log('Model            :', modelName);
console.log('Prompt           :', prompt);

const agent = await client.createAgent(modelName, {
    name: `toolbox-smoke-${Date.now()}`,
    instructions: 'You are a helpful assistant. Call tools when the user asks for information that they expose.',
    tools: [{
        type: 'mcp',
        // Identifier the MCP tool will appear under in the run; also used to attach toolResources below.
        serverLabel: 'aspire_toolbox',
        serverUrl: toolboxRef
    }]
});
console.log('Created agent    :', agent.id);

const thread = await client.threads.create();
console.log('Created thread   :', thread.id);

await client.messages.create(thread.id, 'user', prompt);
console.log('Posted prompt.');

const run = await client.runs.createAndPoll(thread.id, agent.id, {
    // The toolbox already carries its own bearer/headers from the AppHost wiring, so we only need
    // to set requireApproval here so the run does not pause for user approval on each tool call.
    toolResources: {
        mcp: [{
            serverLabel: 'aspire_toolbox',
            headers: {},
            requireApproval: 'never'
        }]
    }
});
console.log('Run status       :', run.status);
console.log('Run last_error   :', JSON.stringify(run.lastError ?? null));

if (run.status === 'completed') {
    const messages = client.messages.list(thread.id, { order: 'desc' });
    for await (const msg of messages) {
        if (msg.role === 'assistant') {
            console.log('\nAssistant >', JSON.stringify(msg.content, null, 2));
            break;
        }
    }
}

console.log('\nCleaning up agent and thread...');
await client.deleteAgent(agent.id);
await client.threads.delete(thread.id);
console.log('Done.');
