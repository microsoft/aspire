// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createConnection } from 'node:net';

/** Real bounded-by-native-lifecycle RESP2 client; no resolved values return to authoring APIs. */
export async function useRedis(host: string, port: number, password: string, operation: 'health' | 'save' | 'write' | 'read'): Promise<void> {
    const socket = createConnection({ host, port });
    let buffered = Buffer.alloc(0);
    let wake: (() => void) | undefined;
    let failure: Error | undefined;
    socket.on('data', bytes => { buffered = Buffer.concat([buffered, bytes]); wake?.(); });
    socket.on('error', error => { failure = error; wake?.(); });
    socket.on('close', () => { failure ??= new Error('Redis socket closed.'); wake?.(); });
    try {
        await new Promise<void>((resolve, reject) => { socket.once('connect', resolve); socket.once('error', reject); });
        async function command(...arguments_: string[]): Promise<string> {
            // RESP2: *<count>\r\n then $<UTF8-byte-length>\r\n<payload>\r\n.
            // Payloads can contain CR/LF. See https://redis.io/docs/latest/develop/reference/protocol-spec/.
            socket.write(Buffer.concat([Buffer.from(`*${arguments_.length}\r\n`), ...arguments_.flatMap(argument => {
                const bytes = Buffer.from(argument);
                return [Buffer.from(`$${bytes.length}\r\n`), bytes, Buffer.from('\r\n')];
            })]));
            while (true) {
                if (failure) { throw new Error('Redis protocol failed.'); }
                const end = buffered.indexOf('\r\n');
                if (end >= 0) {
                    const header = buffered.subarray(0, end).toString();
                    if (header.startsWith('+')) {
                        buffered = buffered.subarray(end + 2);
                        return header.slice(1);
                    }
                    if (header.startsWith('$')) {
                        const size = Number(header.slice(1));
                        if (!Number.isInteger(size) || size < 0 || size > 65536) { throw new Error('Invalid Redis bulk response.'); }
                        if (buffered.length >= end + 2 + size + 2) {
                            if (buffered.subarray(end + 2 + size, end + 4 + size).toString() !== '\r\n') {
                                throw new Error('Invalid Redis bulk terminator.');
                            }
                            const value = buffered.subarray(end + 2, end + 2 + size).toString();
                            buffered = buffered.subarray(end + 4 + size);
                            return value;
                        }
                    } else {
                        throw new Error('Redis rejected the command.');
                    }
                }
                if (buffered.length > 65540) { throw new Error('Redis response exceeds capacity.'); }
                await new Promise<void>(resolve => { wake = resolve; });
                wake = undefined;
            }
        }
        if (await command('AUTH', password) !== 'OK' || await command('PING') !== 'PONG') {
            throw new Error('Redis authentication/readiness failed.');
        }
        if (operation === 'save') {
            if (await command('SAVE') !== 'OK') {
                throw new Error('Redis final snapshot failed.');
            }
        } else if (operation !== 'health') {
            const value = 'native-persistent-value';
            if (operation === 'write' && await command('SET', 'native-integration-key', value) !== 'OK') {
                throw new Error('Redis write failed.');
            }
            if (await command('GET', 'native-integration-key') !== value) {
                throw new Error('Redis persistence failed.');
            }
        }
    } finally {
        socket.destroy();
    }
}
