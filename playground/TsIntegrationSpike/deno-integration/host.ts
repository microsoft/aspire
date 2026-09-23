// Generated host wrapper.
//
// Real integration authors write integration.ts. The CLI/codegen can emit this
// host.ts around one or more integrations so package authors don't own the
// socket, auth, and JSON-RPC boilerplate.

import denoIntegration from './integration.js';
import { runIntegrationHost } from '../kafka-integration/host-runtime.js';

await runIntegrationHost({
    packageName: '@spike/aspire-deno',
    integrations: [denoIntegration],
});
