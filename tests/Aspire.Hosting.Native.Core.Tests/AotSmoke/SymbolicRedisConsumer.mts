// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createConnection } from 'node:net';
import { createServer } from 'node:http';

// Validation consumer only: credentials and endpoint references arrive through
// ordinary workload environment, never invitations, workers or proof-file IPC.
try {
    const connection = new URL(process.env.NATIVE_REDIS_URI!);
    const password = decodeURIComponent(connection.password);
    const socket = createConnection({ host: connection.hostname, port: Number(connection.port) });
    const command = (...parts: string[]) => Buffer.from(
        `*${parts.length}\r\n${parts.map(part => `$${Buffer.byteLength(part)}\r\n${part}\r\n`).join('')}`);
    // Redis RESP2 replies to unauthenticated PING, AUTH, PING, SET and GET:
    // -NOAUTH Authentication required.\r\n+OK\r\n+PONG\r\n+OK\r\n$8\r\nverified\r\n
    const expected = Buffer.from('-NOAUTH Authentication required.\r\n+OK\r\n+PONG\r\n+OK\r\n$8\r\nverified\r\n');
    await new Promise<void>((resolve, reject) => {
        let received = Buffer.alloc(0);
        socket.once('error', reject);
        socket.once('connect', () => socket.write(Buffer.concat([
            command('PING'), command('AUTH', password), command('PING'),
            command('SET', 'native-symbolic-contract', 'verified'), command('GET', 'native-symbolic-contract')
        ])));
        socket.on('data', chunk => {
            received = Buffer.concat([received, chunk]);
            if (received.length > expected.length || !expected.subarray(0, received.length).equals(received)) {
                reject(new Error('Redis protocol validation failed.'));
                socket.destroy();
            } else if (received.length === expected.length) {
                socket.end();
                resolve();
            }
        });
        socket.once('close', () => {
            if (received.length !== expected.length) reject(new Error('Redis response was incomplete.'));
        });
    });
    const server = createServer((_request, response) => {
        response.writeHead(200, { 'content-type': 'text/plain' });
        response.end('verified\n');
    });
    server.listen(Number(process.env.PORT), '127.0.0.1');
    process.once('SIGTERM', () => server.close());
} catch {
    // Do not include URL parsing or protocol errors that could carry credentials.
    console.error('Symbolic Redis consumer failed.');
    process.exitCode = 1;
}
