// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { writeFile } from 'node:fs/promises';
import { getAspireExport } from './base.mjs';
import { runIntegrationHost } from './integration-host.mjs';
import { integration } from './redis.mjs';
import { useRedis } from './redis-protocol.mjs';

if (process.argv[2] === '--emit-contract') {
    await writeFile(process.argv[3], JSON.stringify({ protocolVersion: 2, capabilities: integration.capabilities.map(fn => {
        const metadata = getAspireExport(fn)!;
        return { id: metadata.id, method: metadata.method, description: metadata.description, ...metadata.projection };
    }) }));
} else if (process.argv[2] === '--redis-health' || process.argv[2] === '--redis-save' || process.argv[2] === '--redis-consume') {
    try {
        if (process.argv[2] === '--redis-health' || process.argv[2] === '--redis-save') {
            await useRedis(process.env.REDIS_HOST!, Number(process.env.REDIS_PORT), process.env.REDIS_PASSWORD!,
                process.argv[2] === '--redis-save' ? 'save' : 'health');
        } else {
            // Split only the first ",password=" marker; the rest is an unmodified secret.
            const connection = process.env.ConnectionStrings__cache!;
            const marker = connection.indexOf(',password=');
            const address = connection.slice(0, marker);
            const separator = address.lastIndexOf(':');
            await useRedis(address.slice(0, separator).replace(/^\[|\]$/g, ''), Number(address.slice(separator + 1)),
                connection.slice(marker + 10), process.argv[3] as 'write' | 'read');
        }
    } catch {
        process.exitCode = 1;
    }
} else {
    await runIntegrationHost({ packageName: 'example.ts.redis', integrations: [integration] });
}
