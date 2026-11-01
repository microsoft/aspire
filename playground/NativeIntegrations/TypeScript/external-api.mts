// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { AspireExport, defineIntegration } from './base.mjs';
import type { Composition, DeclaredResource, DeclaredValue } from './aspire.mjs';
import { IntegrationResource } from './integration-host.mjs';
import { nativeTypes } from './native-types.mjs';

const resourceType = { typeId: 'example.ts.external-api/ExternalApiResource', category: 'Handle' } as const;
const text = { typeId: 'string', category: 'Primitive' } as const;

/** Integration-owned resource; native declarations and secret values remain on the server. */
export class ExternalApiResource extends IntegrationResource {
    constructor(readonly nativeDeclaration: DeclaredResource) {
        super(nativeDeclaration, resourceType.typeId);
    }
}

export const addExternalApi = AspireExport({
    id: 'example.ts.external-api/addExternalApi', method: 'addExternalApi',
    description: 'Declares an external HTTP API without provisioning a remote service.',
    projection: {
        parameters: [{ name: 'composition', type: nativeTypes.Composition }, { name: 'name', type: text }, { name: 'baseUrl', type: text }],
        returnType: resourceType
    }
}, async ({ composition, name, baseUrl }: { composition: Composition; name: string; baseUrl: string }) => {
    const endpoint = new URL(baseUrl);
    if (!['http:', 'https:'].includes(endpoint.protocol) || endpoint.username || endpoint.password) {
        throw new Error('An external API requires an HTTP(S) URL without credentials.');
    }
    const declaration = await composition.addResource(name, 'example/external-api');
    await declaration.setResourceConfiguration({ properties: [{ name: 'baseUrl', value: endpoint.href }] });
    return new ExternalApiResource(declaration);
});

export const withApiKey = AspireExport({
    id: 'example.ts.external-api/withApiKey', method: 'withApiKey',
    description: 'Associates a server-owned secret value without resolving it in integration code.',
    projection: {
        targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }, { name: 'apiKey', type: nativeTypes.DeclaredValue }],
        returnType: resourceType
    }
}, async ({ context, apiKey }: { context: ExternalApiResource; apiKey: DeclaredValue }) => {
    await context.nativeDeclaration.setSymbolicConfiguration('apiKey', apiKey);
    return context;
});

export const getResourceName = AspireExport({
    id: 'example.ts.external-api/getResourceName', method: 'getResourceName',
    description: 'Reads the authoritative native resource name.',
    projection: {
        targetTypeId: resourceType.typeId, targetParameterName: 'context',
        parameters: [{ name: 'context', type: resourceType }], returnType: text
    }
}, async ({ context }: { context: ExternalApiResource }) => (await context.nativeDeclaration.inspectResource()).name!);

export const integration = defineIntegration({ name: 'ExternalApi', capabilities: [addExternalApi, withApiKey, getResourceName] });
