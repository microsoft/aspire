import { describe, expect, it } from 'vitest';
import { getAspireExport } from '@aspire/base';
import integration, {
    addNuxtApp,
    runtimeConfigEnvironmentName,
    withNuxtReference,
} from '../../../src/Aspire.Hosting.Nuxt/src/integration.js';

describe('Nuxt integration', () => {
    it.each([
        ['apiBase', 'NUXT_API_BASE'],
        ['api.baseURL', 'NUXT_API_BASE_URL'],
        ['public.apiBase', 'NUXT_PUBLIC_API_BASE'],
        ['apiV2.baseURL', 'NUXT_API_V2_BASE_URL'],
    ])('maps %s to %s', (path, environmentName) => {
        expect(runtimeConfigEnvironmentName(path)).toBe(environmentName);
    });

    it.each(['', '.api', 'api.', 'api..base', 'api/base', 'api-base', 'api base', 'api\nbase', 'api\n', 'api\r\n', '2api'])(
        'rejects invalid runtime config path %j',
        path => {
            expect(() => runtimeConfigEnvironmentName(path)).toThrow('Invalid Nuxt runtimeConfig path');
        },
    );

    it('exports builder creation and endpoint-based runtime configuration', () => {
        expect(integration.name).toBe('NuxtIntegration');
        expect(integration.capabilities).toEqual([addNuxtApp, withNuxtReference]);
        expect(integration.capabilities.map(capability => getAspireExport(capability))).toMatchSnapshot();
    });
});
