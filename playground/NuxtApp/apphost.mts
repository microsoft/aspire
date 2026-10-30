import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
await builder.addDockerComposeEnvironment('compose');

const api = await builder.addJavaScriptApp('api', './api', { runScriptName: 'start' })
    .withHttpEndpoint({ env: 'PORT' })
    .withHttpHealthCheck({ path: '/health' })
    .publishAsNodeServer('server.mjs');

await builder.addNuxtApp('web', './web')
    .withNuxtReference('apiBase', api.getEndpoint('http'))
    .withHttpHealthCheck({ path: '/api/health' })
    .withExternalHttpEndpoints()
    .waitFor(api);

await builder.build().run();
