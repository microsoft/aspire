import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureSandboxGroup('sandboxes');

await builder.addDockerfile('site', './site')
    .withHttpEndpoint({ name: 'http', targetPort: 80 })
    .withExternalHttpEndpoints();

await builder.build().run();
