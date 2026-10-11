import { randomUUID } from 'node:crypto';
import { createConnection } from 'node:net';

// Exercise real Redis without adding a client package to the Nuxt fixture.
// RESP2 replies used here are +OK\r\n, -ERR...\r\n, or $<UTF-8 bytes>\r\n<value>\r\n.
// TCP chunks may split either the length line or the bulk value.
export default defineEventHandler(async (event) => {
    const endpoint = new URL(useRuntimeConfig(event).redisUri);
    if (endpoint.protocol !== 'redis:') {
        throw new Error('This feasibility fixture supports non-TLS Redis only.');
    }

    const socket = createConnection({ host: endpoint.hostname, port: Number(endpoint.port) });
    socket.setTimeout(10000, () => socket.destroy(new Error('Redis operation timed out.')));
    const replies: string[] = [];
    let buffer = Buffer.alloc(0);
    const key = `native-hosting:${randomUUID()}`;
    const expected = 'Nuxt read/write through an owner-resolved Redis reference';
    const commands: string[][] = [];
    if (endpoint.password) {
        commands.push(['AUTH', decodeURIComponent(endpoint.password)]);
    }
    commands.push(['SET', key, expected, 'EX', '30'], ['GET', key]);

    try {
        return await new Promise<{ value: string; commands: number }>((resolve, reject) => {
            socket.on('error', reject);
            socket.on('end', () => reject(new Error('Redis disconnected before completing the round trip.')));
            socket.on('connect', () => {
                for (const command of commands) {
                    socket.write(`*${command.length}\r\n` + command.map(value =>
                        `$${Buffer.byteLength(value)}\r\n${value}\r\n`).join(''));
                }
            });
            socket.on('data', (chunk) => {
                buffer = Buffer.concat([buffer, chunk]);
                while (buffer.length > 0) {
                    const lineEnd = buffer.indexOf('\r\n');
                    if (lineEnd < 0) return;
                    const prefix = buffer[0];
                    const line = buffer.subarray(1, lineEnd).toString('utf8');
                    if (prefix === 45) {
                        reject(new Error('Redis rejected the test operation.'));
                        return;
                    }
                    if (prefix === 36) {
                        const length = Number(line);
                        if (!Number.isSafeInteger(length) || length < 0) {
                            reject(new Error('Expected a non-null Redis bulk reply.'));
                            return;
                        }
                        if (buffer.length < lineEnd + 2 + length + 2) return;
                        if (buffer.subarray(lineEnd + 2 + length, lineEnd + 4 + length).toString() !== '\r\n') {
                            reject(new Error('Malformed Redis bulk reply terminator.'));
                            return;
                        }
                        replies.push(buffer.subarray(lineEnd + 2, lineEnd + 2 + length).toString('utf8'));
                        buffer = buffer.subarray(lineEnd + 4 + length);
                    } else if (prefix === 43) {
                        replies.push(line);
                        buffer = buffer.subarray(lineEnd + 2);
                    } else {
                        reject(new Error('Unexpected Redis reply type.'));
                        return;
                    }
                    if (replies.length === commands.length) {
                        if (replies.slice(0, -1).some(reply => reply !== 'OK') || replies.at(-1) !== expected) {
                            reject(new Error('Redis round trip returned an unexpected value.'));
                            return;
                        }
                        resolve({ value: expected, commands: commands.length });
                        return;
                    }
                }
            });
        });
    } finally {
        socket.destroy();
    }
});
