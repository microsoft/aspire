import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const apiKey = await builder.addParameter("openai-api-key", { secret: true });
const openai = await builder.addOpenAI("openai")
    .withEndpoint("https://api.openai.com/v1")
    .withApiKey(apiKey);

await openai.addModel("chat-model", "gpt-4o-mini");

await builder.build().run();
