// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createConnection } from 'node:net';

export function redisCommand(host: string, port: number, args: string[]): Promise<string> {
    return new Promise((resolve, reject) => {
        const socket = createConnection({ host, port });
        const timeout = setTimeout(() => finish(new Error('Redis command timed out.')), 5000);
        let buffer = Buffer.alloc(0);
        let settled = false;
        function finish(error?: Error, value?: string): void {
            if (settled) return;
            settled = true;
            clearTimeout(timeout);
            socket.destroy();
            if (error) reject(error);
            else resolve(value!);
        }
        socket.once('error', error => finish(error));
        socket.once('connect', () => {
            // Redis RESP2 requests use byte-counted bulk strings:
            //   *2\r\n$3\r\nGET\r\n$5\r\nproof\r\n
            // See https://redis.io/docs/latest/develop/reference/protocol-spec/
            socket.write(`*${args.length}\r\n` + args.map(arg =>
                `$${Buffer.byteLength(arg)}\r\n${arg}\r\n`).join(''));
        });
        socket.on('data', chunk => {
            buffer = Buffer.concat([buffer, Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)]);
            if (buffer.length > 65536) {
                finish(new Error('Redis response exceeds the fixture limit.'));
                return;
            }
            const end = buffer.indexOf('\r\n');
            if (end < 0) return;
            const header = buffer.subarray(0, end).toString('utf8');
            if (header.startsWith('+')) {
                finish(undefined, header.slice(1));
            } else if (header.startsWith('$')) {
                const length = Number(header.slice(1));
                if (!Number.isSafeInteger(length) || length < 0 || length > 65536) {
                    finish(new Error('Redis returned an invalid bulk-string length.'));
                } else if (buffer.length >= end + 2 + length + 2) {
                    finish(undefined, buffer.subarray(end + 2, end + 2 + length).toString('utf8'));
                }
            } else {
                finish(new Error('Redis returned an unexpected response.'));
            }
        });
        socket.once('end', () => finish(new Error('Redis closed the connection before completing its response.')));
    });
}
