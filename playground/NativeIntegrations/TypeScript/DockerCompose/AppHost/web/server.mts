// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createServer } from 'node:http';
import { createConnection } from 'node:net';

const connection = process.env.CACHE_URL ?? process.env.ConnectionStrings__cache;
if (!connection) throw new Error('The cache connection is required.');
const cache = new URL(connection);

async function command(operation: string[]): Promise<string> {
    return await new Promise((resolve, reject) => {
        const socket = createConnection({ host: cache.hostname, port: Number(cache.port) || 6379 });
        const commands = [['AUTH', decodeURIComponent(cache.password)], operation];
        let pending = '', replies = 0;
        const fail = (error: Error): void => { socket.destroy(); reject(error); };
        socket.setTimeout(5000, () => fail(new Error('Cache request timed out.')));
        socket.once('error', () => fail(new Error('Cache connection failed.')));
        socket.once('connect', () => {
            // RESP arrays: *2\r\n$4\r\nAUTH\r\n$<UTF-8 byte length>\r\n<password>\r\n.
            // This sample only expects simple-string/integer AUTH/PING/INCR replies.
            socket.write(commands.map(parts => `*${parts.length}\r\n${parts.map(part =>
                `$${Buffer.byteLength(part)}\r\n${part}\r\n`).join('')}`).join(''));
        });
        socket.on('data', data => {
            pending += data.toString('utf8');
            let boundary: number;
            while ((boundary = pending.indexOf('\r\n')) >= 0) {
                const reply = pending.slice(0, boundary);
                pending = pending.slice(boundary + 2);
                if (!reply.startsWith('+') && !reply.startsWith(':')) {
                    fail(new Error('Cache authentication or command failed.'));
                    return;
                }
                if (++replies === commands.length) {
                    socket.end();
                    resolve(reply.slice(1));
                }
            }
        });
        socket.once('end', () => {
            if (replies < commands.length) fail(new Error('Cache closed before replying.'));
        });
    });
}

createServer(async (request, response) => {
    try {
        const value = await command(request.url === '/health' ? ['PING'] : ['INCR', 'visits']);
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ value }));
    } catch {
        response.writeHead(503);
        response.end('Cache unavailable.');
    }
}).listen(Number(process.env.PORT ?? 8080), '0.0.0.0');
