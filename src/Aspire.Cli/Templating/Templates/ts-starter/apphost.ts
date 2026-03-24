import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Run the Express API and expose its HTTP endpoint externally.
const app = await builder
    .addNodeApp("app", "./api", "src/index.ts")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

// Run the Vite frontend after the API and inject the API URL for local proxying.
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withReference(app)
    .waitFor(app);

// Bundle the frontend build output into the API container for publish/deploy.
await app.publishWithContainerFiles(frontend, "./static");

await builder.build().run();
