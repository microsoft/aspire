import type {
    DistributedApplicationBuilder,
    EndpointReference,
    JavaScriptAppResource,
} from '../.aspire/modules/aspire.mjs';
import {
    AspireExport,
    defineIntegration,
    type AspireTypeRef,
} from '../.aspire/modules/base.mjs';

const builderType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder',
    category: 'Handle',
    isInterface: true,
};
const appType: AspireTypeRef = {
    typeId: 'Aspire.Hosting.JavaScript/Aspire.Hosting.JavaScript.JavaScriptAppResource',
    category: 'Handle',
};
const endpointType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference',
    category: 'Handle',
};
const stringType: AspireTypeRef = { typeId: 'string', category: 'Primitive' };

interface AddNuxtAppArgs {
    builder: DistributedApplicationBuilder;
    name: string;
    appDirectory: string;
    runScriptName?: string;
    buildScriptName?: string;
}

/** Adds a Nuxt dev server and publishes its standalone Nitro Node server. */
export const addNuxtApp = AspireExport<AddNuxtAppArgs, JavaScriptAppResource>(
    {
        id: '@aspire/nuxt/addNuxtApp',
        method: 'addNuxtApp',
        description: 'Adds a Nuxt application with a development HTTP endpoint and standalone Nitro publishing.',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: builderType.typeId,
            targetType: builderType,
            targetParameterName: 'builder',
            returnsBuilder: true,
            returnType: appType,
            parameters: [
                { name: 'name', type: stringType },
                { name: 'appDirectory', type: stringType },
                { name: 'runScriptName', type: stringType, isOptional: true },
                { name: 'buildScriptName', type: stringType, isOptional: true },
            ],
        },
    },
    async ({ builder, name, appDirectory, runScriptName = 'dev', buildScriptName = 'build' }) => {
        // Both nuxt dev and Nitro honor PORT/HOST. Let Aspire allocate PORT rather
        // than capturing a development URL in the published model.
        // https://nuxt.com/docs/4.x/api/commands/dev
        // https://nuxt.com/docs/4.x/getting-started/deployment#nodejs-server
        return await builder.addJavaScriptApp(name, appDirectory, { runScriptName })
            .withBuildScript(buildScriptName)
            .withHttpEndpoint({ env: 'PORT' })
            .withEnvironment('HOST', '0.0.0.0')
            .publishAsNodeServer('.output/server/index.mjs', { outputPath: '.output' });
    },
);

interface WithNuxtReferenceArgs {
    resource: JavaScriptAppResource;
    runtimeConfigPath: string;
    endpoint: EndpointReference;
}

/** Binds a Nuxt runtimeConfig value to a deferred service endpoint. */
export const withNuxtReference = AspireExport<WithNuxtReferenceArgs, JavaScriptAppResource>(
    {
        id: '@aspire/nuxt/withNuxtReference',
        method: 'withNuxtReference',
        description: 'Injects a service endpoint into Nuxt runtimeConfig, preserving run and deployment endpoint resolution.',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: appType.typeId,
            targetType: appType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: appType,
            parameters: [
                { name: 'runtimeConfigPath', type: stringType },
                { name: 'endpoint', type: endpointType },
            ],
        },
    },
    async ({ resource, runtimeConfigPath, endpoint }) => {
        const environmentName = runtimeConfigEnvironmentName(runtimeConfigPath);
        return await resource.withReference(endpoint).withEnvironment(environmentName, endpoint);
    },
);

export function runtimeConfigEnvironmentName(path: string): string {
    if (path !== path.trim() || !/^[a-zA-Z][a-zA-Z0-9]*(?:\.[a-zA-Z][a-zA-Z0-9]*)*$/.test(path)) {
        throw new Error(`Invalid Nuxt runtimeConfig path '${path}'. Use dotted camelCase keys, for example 'apiBase' or 'public.apiBase'.`);
    }

    // runtimeConfig: { api: { baseURL: '' } } maps to NUXT_API_BASE_URL.
    // The key must already exist in nuxt.config.ts for runtime overrides to apply.
    // https://nuxt.com/docs/4.x/guide/going-further/runtime-config#environment-variables
    return 'NUXT_' + path
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1_$2')
        .replace(/([a-z0-9])([A-Z])/g, '$1_$2')
        .replaceAll('.', '_')
        .toUpperCase();
}

export default defineIntegration({
    name: 'NuxtIntegration',
    capabilities: [addNuxtApp, withNuxtReference],
});
