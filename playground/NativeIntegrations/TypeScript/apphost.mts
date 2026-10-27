// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { connectNativeAppHost } from './native-client.mjs';
import { IntegrationClient } from './provider-client.mjs';

const port = Number(process.argv[2]);
const token = process.env.ASPIRE_NATIVE_RPC_TOKEN;
if (!token) {
    throw new Error('Set ASPIRE_NATIVE_RPC_TOKEN for the native server.');
}
const native = await connectNativeAppHost({
    endpoint: { host: '127.0.0.1', port }, authenticationToken: token, authenticationTimeoutMilliseconds: 15000
});
const provider = await native.activateProvider('example.ts.external-api', process.execPath, [process.argv[3]]);
try {
    const session = await native.server.openApplication();
    const composition = await session.startGeneration();
    const apiKey = await composition.addSecretParameter('catalog-api-key', 'example-authoring-key');
    const integration = new IntegrationClient(native.client);
    const catalog = await integration.addExternalApi(composition, 'catalog', 'https://catalog.example/');
    await catalog.withApiKey(apiKey);
    console.log(await catalog.getResourceName());
    await session.retireGeneration(composition);
} finally {
    await provider.close();
    await native.client.disconnect();
}
