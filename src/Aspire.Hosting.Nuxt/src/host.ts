import nuxtIntegration from './integration.js';
import { runIntegrationHost } from '../.aspire/modules/integration-host.mjs';

await runIntegrationHost({
    packageName: '@aspire/nuxt',
    integrations: [nuxtIntegration],
});
