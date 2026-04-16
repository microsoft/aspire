// Generated host wrapper.
//
// Real integration authors write integration.ts. The CLI/codegen can emit this
// host.ts around one or more integrations so package authors don't own the
// socket, auth, and JSON-RPC boilerplate.

import kafkaIntegration from './integration.js';
import { runIntegrationHost } from './host-runtime.js';

await runIntegrationHost({
    packageName: '@spike/aspire-kafka',
    integrations: [kafkaIntegration],
});
