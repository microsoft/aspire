// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync, statSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { createSession } from './aspire.mjs';
import { CapabilityError, isAtsError, wrapIfHandle } from './transport.mjs';
import type { AspireClientRpc } from './transport.mjs';

const binary = process.argv[2];
if (!binary) {
    throw new Error('Pass the published NativeAOT smoke executable.');
}

// The fixture bridges generated wrappers to the smoke process's line-delimited
// JSON-RPC transport. It does not replace SDK methods or native dispatch.
class NativeClient implements AspireClientRpc {
    readonly token = randomBytes(32).toString('hex');
    readonly process = spawn(binary, ['--rpc'], {
        env: { ...process.env, ASPIRE_NATIVE_RPC_TOKEN: this.token },
        stdio: ['pipe', 'pipe', 'pipe']
    });
    connected = true;
    throwOnPendingRejections = true;
    private nextId = 0;
    private pending = new Map<number, { resolve(value: unknown): void; reject(error: Error): void }>();
    private tracked = new Set<Promise<unknown>>();
    private failures: unknown[] = [];

    constructor() {
        createInterface({ input: this.process.stdout }).on('line', line => {
            const response: unknown = JSON.parse(line);
            if (!response || typeof response !== 'object' || !('id' in response) || typeof response.id !== 'number') {
                throw new Error('Invalid native JSON-RPC response.');
            }
            const pending = this.pending.get(response.id);
            if (!pending) {
                throw new Error('Unexpected native JSON-RPC response ID.');
            }
            this.pending.delete(response.id);
            if ('error' in response) {
                pending.reject(new Error(JSON.stringify(response.error)));
            } else if ('result' in response) {
                pending.resolve(response.result);
            } else {
                pending.reject(new Error('The native response has neither result nor error.'));
            }
        });
        this.process.stderr.on('data', data => process.stderr.write(data));
        this.process.on('error', error => this.fail(error));
        this.process.on('exit', code => {
            this.connected = false;
            if (this.pending.size > 0) {
                this.fail(new Error(`Native RPC process exited with pending calls (${code}).`));
            }
        });
    }

    call(method: string, parameters: unknown): Promise<unknown> {
        const id = ++this.nextId;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            this.process.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: parameters }) + '\n');
        });
    }

    async invokeCapability<T = unknown>(capabilityId: string, args?: Record<string, unknown>): Promise<T> {
        // This slice has only strings and handles as inputs. Generated wrappers
        // and Handle both implement toJSON; callbacks/cancellation are not advertised.
        const result = await this.call('invokeCapability', [capabilityId, args ?? {}]);
        if (isAtsError(result)) {
            throw new CapabilityError(result.$error);
        }
        return wrapIfHandle(result, this) as T;
    }

    async cancelToken(): Promise<boolean> {
        throw new Error('Cancellation capabilities are not implemented in this slice.');
    }

    trackPromise(promise: Promise<unknown>): void {
        this.tracked.add(promise);
        void promise.then(
            () => this.tracked.delete(promise),
            error => {
                this.tracked.delete(promise);
                this.failures.push(error);
            });
    }

    async flushPendingPromises(): Promise<void> {
        await Promise.all(this.tracked);
        if (this.throwOnPendingRejections && this.failures.length > 0) {
            throw new AggregateError(this.failures, 'Generated SDK calls failed.');
        }
    }

    private fail(error: Error): void {
        for (const pending of this.pending.values()) {
            pending.reject(error);
        }
        this.pending.clear();
    }
}

const client = new NativeClient();
const timeout = setTimeout(() => client.process.kill(), 60_000);
try {
    await assert.rejects(client.call('getCapabilities', []));
    assert.equal(await client.call('authenticate', ['incorrect-token']), false);
    assert.equal(await client.call('authenticate', [client.token]), true);
    const contract = await client.call('getCapabilities', []);
    assert.deepEqual(contract, JSON.parse(readFileSync(new URL('../contract.json', import.meta.url), 'utf8')));
    assert.ok(contract && typeof contract === 'object' && 'Capabilities' in contract && Array.isArray(contract.Capabilities));
    const compositionMethods = new Set(['createSession', 'startGeneration', 'retireGeneration', 'addResource', 'waitFor', 'inspectResource', 'inspect', 'seal']);
    assert.equal(contract.Capabilities.filter((capability: unknown) => capability && typeof capability === 'object' &&
        'MethodName' in capability && typeof capability.MethodName === 'string' && compositionMethods.has(capability.MethodName)).length, 8);
    const expectFailure = async (operation: string, arguments_: Record<string, unknown>, code: string) =>
        assert.rejects(client.invokeCapability(
            (operation === 'createSession' ? 'Aspire.Hosting.Native.Server/' : 'Aspire.Hosting.Native.Api/') + operation, arguments_), error =>
            error instanceof CapabilityError && error.code === code);
    await expectFailure('missingCapability', {}, 'CAPABILITY_NOT_FOUND');
    await expectFailure('createSession', { unexpected: true }, 'INVALID_ARGUMENT');
    const session = await createSession(client);
    const composition = await session.startGeneration();
    const cache = await composition.addResource('cache', 'example/Redis');
    const web = await composition.addResource('web', 'example/Web');
    await expectFailure('addResource', { context: composition, name: 'cache', typeId: 'example/Redis' }, 'OPERATION_REJECTED');
    await expectFailure('addResource', { context: composition, name: 'invalid name', typeId: 'example/Redis' }, 'INVALID_ARGUMENT');
    await expectFailure('inspectResource', { context: { ...cache.toJSON(), $handle: 'not-issued' } }, 'HANDLE_NOT_FOUND');
    await expectFailure('inspectResource', { context: { ...cache.toJSON(), $type: composition.toJSON().$type } }, 'TYPE_MISMATCH');
    await web.waitFor(cache);
    await expectFailure('waitFor', { context: cache, dependency: web }, 'OPERATION_REJECTED');
    const otherSession = await createSession(client);
    await expectFailure('retireGeneration', { context: otherSession, composition }, 'INVALID_ARGUMENT');
    const snapshot = await composition.seal();
    assert.ok(snapshot.resources);
    assert.deepEqual(snapshot.resources.map(resource => resource.name), ['cache', 'web']);
    assert.deepEqual(snapshot.resources[1].dependencies, [snapshot.resources[0].resourceId]);
    assert.deepEqual(await composition.inspect(), snapshot);
    await expectFailure('addResource', { context: composition, name: 'late', typeId: 'example/Redis' }, 'OPERATION_REJECTED');
    await session.retireGeneration(composition);
    await assert.rejects(cache.inspectResource(), error =>
        error instanceof CapabilityError && error.code === 'HANDLE_NOT_FOUND');
    for (let index = 0; index < 100; index++) {
        const replacement = await session.startGeneration();
        await replacement.addResource('cache', 'example/Redis');
        await session.retireGeneration(replacement);
    }
    const replacement = await session.startGeneration();
    assert.deepEqual((await replacement.inspect()).resources, []);
    await client.flushPendingPromises();
    assert.ok(client.process.pid);
    const residentSamples = Array.from({ length: 3 }, () =>
        Number(execFileSync('ps', ['-o', 'rss=', '-p', String(client.process.pid)], { encoding: 'utf8' }).trim()) * 1024);
    console.log(JSON.stringify({
        generatedSdkNativeRoundTrips: 'passed',
        generationReplacements: 101,
        binaryBytes: statSync(binary).size,
        residentSamples
    }, null, 2));
} finally {
    const exited = client.process.exitCode === null && client.process.signalCode === null
        ? once(client.process, 'exit')
        : Promise.resolve([client.process.exitCode]);
    client.process.stdin.end();
    const [code] = await exited;
    clearTimeout(timeout);
    assert.equal(code, 0);
}
