// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { createServer } from 'node:http';

const name = process.argv[2];
const peerName = name === 'alpha' ? 'beta' : 'alpha';
const self = process.env.SELF_URL;
const peer = process.env.PEER_URL;
assert.ok(name && self && peer);
assert.equal(process.env[`ConnectionStrings__${peerName}`], peer);
assert.equal(process.argv[3], peer, 'Symbolic arguments must resolve to the same allocated peer address.');
const port = Number(process.env.PORT);
assert.ok(Number.isInteger(port) && port > 0 && port <= 65535);
const server = createServer(async (request, response) => {
    response.setHeader('content-type', 'application/json');
    if (request.url === '/identity') {
        response.end(JSON.stringify({ name, self, peer,
            ...(process.env.REVISION ? { revision: process.env.REVISION } : {}) }));
        return;
    }
    if (request.url === '/peer') {
        try {
            const result = await fetch(`${peer}/identity`, { signal: AbortSignal.timeout(5000) });
            assert.equal(result.status, 200);
            response.end(await result.text());
        } catch (error) {
            console.error('Peer request failed', error);
            response.statusCode = 500;
            response.end(JSON.stringify({ error: 'Peer request failed' }));
        }
        return;
    }
    response.statusCode = 404;
    response.end(JSON.stringify({ error: 'Unknown route' }));
});
server.listen(port, '127.0.0.1');
