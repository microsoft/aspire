// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { createConnection, createServer } from 'node:net';

const port = Number(process.env.FORWARD_PORT);
const targetPort = Number(process.argv[2]);
assert.ok(Number.isInteger(port) && port > 0);
assert.ok(Number.isInteger(targetPort) && targetPort > 0);
const sockets = new Set<ReturnType<typeof createConnection>>();
const server = createServer(incoming => {
    const outgoing = createConnection({ host: '127.0.0.1', port: targetPort });
    sockets.add(incoming);
    sockets.add(outgoing);
    incoming.once('error', () => outgoing.destroy());
    outgoing.once('error', () => incoming.destroy());
    incoming.once('close', () => { sockets.delete(incoming); outgoing.destroy(); });
    outgoing.once('close', () => { sockets.delete(outgoing); incoming.destroy(); });
    incoming.pipe(outgoing).pipe(incoming);
});
server.listen(port, '127.0.0.1', () => console.log('Local tunnel fixture is forwarding.'));
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
    process.once(signal, () => {
        for (const socket of sockets) socket.destroy();
        server.close();
    });
}
