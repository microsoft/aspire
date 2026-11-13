// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { writeFile } from 'node:fs/promises';
import { getAspireExport } from './base.mjs';
import { runIntegrationHost } from './integration-host.mjs';
import { integration } from './external-api.mjs';

if (process.argv[2] === '--emit-contract') {
    // Offline metadata emission: deployment/runtime never reflect over the provider.
    await writeFile(process.argv[3], JSON.stringify({
        protocolVersion: 2,
        capabilities: integration.capabilities.map(fn => {
            const metadata = getAspireExport(fn)!;
            return { id: metadata.id, method: metadata.method, description: metadata.description, ...metadata.projection };
        })
    }, null, 2));
} else {
    await runIntegrationHost({ packageName: 'example.ts.external-api', integrations: [integration] });
}
