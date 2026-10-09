import { EventEmitter } from 'node:events';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AspireExport, defineIntegration } from '@aspire/base';
import { CancellationToken, Handle, invokeRegisteredCallback, registerCallback, registerCancellation, registerHandleWrapper, unregisterCallback, type AspireClientRpc } from '@aspire/transport';
import { runIntegrationHost } from '../../../src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/integration-host.mts';

const { onRequest, sendRequest, onClose } = vi.hoisted(() => ({
    onRequest: vi.fn(),
    sendRequest: vi.fn(),
    onClose: vi.fn(),
}));

vi.mock('node:net', () => ({
    createConnection: () => {
        const socket = Object.assign(new EventEmitter(), { ref: vi.fn(), unref: vi.fn() });
        queueMicrotask(() => socket.emit('connect'));
        return socket;
    },
}));

vi.mock('vscode-jsonrpc/node.js', () => ({
    RequestType3: class {
        constructor(readonly method: string) {}
    },
    StreamMessageReader: class {},
    StreamMessageWriter: class {},
    createMessageConnection: () => ({
        onRequest,
        sendRequest,
        onError: vi.fn(),
        onClose,
        listen: vi.fn(),
    }),
}));

describe('integration host callback relay', () => {
    afterEach(() => {
        for (const [close] of onClose.mock.calls) {
            close();
        }
        vi.unstubAllEnvs();
        vi.restoreAllMocks();
        vi.clearAllMocks();
    });

    it.each(['void', 'string'] as const)('applies DTO writeback only for a %s callback across a serialized round trip', async (returnType) => {
        vi.stubEnv('REMOTE_APP_HOST_SOCKET_PATH', 'integration-test-socket');
        vi.stubEnv('ASPIRE_REMOTE_APPHOST_TOKEN', 'integration-test-token');
        vi.stubEnv('ASPIRE_INTEGRATION_HOST_REGISTRATION_ID', 'integration-test-registration');
        vi.spyOn(process, 'on').mockReturnValue(process);
        vi.spyOn(console, 'log').mockImplementation(() => {});
        let client!: AspireClientRpc;
        registerHandleWrapper('test/DtoClient', (_handle, transport) => {
            client = transport;
            return transport;
        });
        const options = { name: 'default', untouched: 'retained', nested: { value: 'original' } };
        const second = { name: 'second' };
        const nonDto = { name: 'not-a-dto' };
        const callbackId = registerCallback((first: typeof options, other: typeof second, unprojected: typeof nonDto, nullable: unknown) => {
            first.name = 'configured-by-guest';
            first.nested = { value: 'configured' };
            other.name = 'second-configured';
            unprojected.name = 'must-not-write-back';
            expect(nullable).toBeNull();
            return returnType === 'string' ? 'callback-result' : undefined;
        });
        sendRequest.mockImplementation(async (method: string, id: string, args: unknown) => {
            if (method !== 'invokeGuestCallback') {
                return true;
            }
            // Serialize both ways so a shared object cannot make a missing writeback pass.
            const response = await invokeRegisteredCallback(id, JSON.parse(JSON.stringify(args)), client);
            return JSON.parse(JSON.stringify(response));
        });
        const capability = AspireExport({
            id: 'test/dtoWriteback',
            method: 'dtoWriteback',
            description: 'Exercise callback DTO writeback',
            projection: {
                parameters: [{
                    name: 'configure',
                    isCallback: true,
                    callbackReturnType: { typeId: returnType, category: 'Primitive' },
                    callbackParameters: [
                        { name: 'options', type: { typeId: 'test/Options', category: 'Dto' } },
                        { name: 'second', type: { typeId: 'test/Options', category: 'Dto' } },
                        { name: 'other', type: { typeId: 'object', category: 'Unknown' } },
                        { name: 'nullable', type: { typeId: 'test/Options', category: 'Dto', isNullable: true } },
                    ],
                }],
            },
        }, async ({ configure }: { configure: (...args: unknown[]) => Promise<unknown> }) => {
            const result = await configure(options, second, nonDto, null);
            return { result, options, second, nonDto };
        });
        try {
            await runIntegrationHost({
                packageName: 'test-host',
                integrations: [defineIntegration({ name: 'test', capabilities: [capability] })],
            });
            const handler = onRequest.mock.calls.find(([method]) =>
                typeof method === 'object' && method.method === 'handleExternalCapability')![1];
            const response = await handler('test/dtoWriteback', {
                configure: callbackId,
                probe: { $handle: 'client', $type: 'test/DtoClient' },
            });
            expect(response.result).toBe(returnType === 'void' ? undefined : 'callback-result');
            expect(options).toEqual({
                name: returnType === 'void' ? 'configured-by-guest' : 'default',
                untouched: 'retained',
                nested: { value: returnType === 'void' ? 'configured' : 'original' },
            });
            expect(second).toEqual({ name: returnType === 'void' ? 'second-configured' : 'second' });
            expect(nonDto).toEqual({ name: 'not-a-dto' });
        } finally {
            unregisterCallback(callbackId);
        }
    });

    it.each(['handle', 'nested'] as const)('wraps a %s callback result before invoking integration code', async (shape) => {
        vi.stubEnv('REMOTE_APP_HOST_SOCKET_PATH', 'integration-test-socket');
        vi.stubEnv('ASPIRE_REMOTE_APPHOST_TOKEN', 'integration-test-token');
        vi.stubEnv('ASPIRE_INTEGRATION_HOST_REGISTRATION_ID', 'integration-test-registration');
        vi.spyOn(process, 'on').mockReturnValue(process);
        vi.spyOn(console, 'log').mockImplementation(() => {});

        const typeId = 'test/CallbackResource';
        class CallbackResource {
            constructor(readonly handle: Handle) {}
            name() { return 'callback-resource'; }
        }
        registerHandleWrapper(typeId, handle => new CallbackResource(handle));
        const handle = { $handle: 'resource-1', $type: typeId };
        sendRequest.mockImplementation(async (method: string) =>
            method === 'invokeGuestCallback'
                ? shape === 'handle' ? handle : { resources: [handle] }
                : true);

        let callbackResult: unknown;
        const capability = AspireExport(
            {
                id: 'test/callbackResult',
                method: 'callbackResult',
                description: 'Exercise callback results',
                projection: {
                    returnType: { typeId: 'void', category: 'Primitive' },
                    parameters: [{ name: 'configure', isCallback: true }],
                },
            },
            async ({ configure }: { configure: () => Promise<unknown> }) => {
                callbackResult = await configure();
            });
        await runIntegrationHost({
            packageName: 'test-host',
            integrations: [defineIntegration({ name: 'test', capabilities: [capability] })],
        });
        expect(sendRequest).toHaveBeenCalledWith('registerAsIntegrationHost', 'integration-test-registration');

        const registration = onRequest.mock.calls.find(([method]) =>
            typeof method === 'object' && method.method === 'handleExternalCapability');
        expect(registration).toBeDefined();
        await registration![1]('test/callbackResult', { configure: 'guest-callback' });

        const result = shape === 'handle'
            ? callbackResult
            : (callbackResult as { resources: unknown[] }).resources[0];
        expect(result).toBeInstanceOf(CallbackResource);
        expect((result as CallbackResource).name()).toBe('callback-resource');
        expect((result as CallbackResource).handle.toJSON()).toEqual(handle);
        expect(sendRequest).toHaveBeenCalledWith('invokeGuestCallback', 'guest-callback', {});
    });

    it.each([
        ['authentication', 'Integration host authentication failed.'],
        ['registration', 'Integration host registration was rejected.'],
        ['duplicate', 'Duplicate integration capability: test/duplicate'],
    ])('rejects failed %s instead of advertising a usable host', async (failure, expectedError) => {
        vi.stubEnv('REMOTE_APP_HOST_SOCKET_PATH', 'integration-test-socket');
        vi.stubEnv('ASPIRE_REMOTE_APPHOST_TOKEN', 'integration-test-token');
        vi.stubEnv('ASPIRE_INTEGRATION_HOST_REGISTRATION_ID', 'integration-test-registration');
        vi.spyOn(process, 'on').mockReturnValue(process);
        vi.spyOn(console, 'log').mockImplementation(() => {});
        sendRequest.mockImplementation(async (method: string) =>
            !(method === 'authenticate' && failure === 'authentication') &&
            !(method === 'registerAsIntegrationHost' && failure === 'registration'));
        const capability = AspireExport(
            { id: 'test/duplicate', method: 'duplicate', description: 'Test duplicate exports' },
            async () => {});

        await expect(runIntegrationHost({
            packageName: 'test-host',
            integrations: [defineIntegration({
                name: 'test',
                capabilities: failure === 'duplicate' ? [capability, capability] : [],
            })],
        })).rejects.toThrow(expectedError);
    });

    async function dispatchWithClient(action: (client: AspireClientRpc, name: string) => Promise<unknown>) {
        vi.stubEnv('REMOTE_APP_HOST_SOCKET_PATH', 'integration-test-socket');
        vi.stubEnv('ASPIRE_REMOTE_APPHOST_TOKEN', 'integration-test-token');
        vi.stubEnv('ASPIRE_INTEGRATION_HOST_REGISTRATION_ID', 'integration-test-registration');
        vi.spyOn(process, 'on').mockReturnValue(process);
        vi.spyOn(console, 'log').mockImplementation(() => {});
        vi.spyOn(console, 'warn').mockImplementation(() => {});
        sendRequest.mockResolvedValue(true);
        registerHandleWrapper('test/ClientProbe', (_handle, client) => client);
        const capability = AspireExport(
            { id: 'test/client', method: 'client', description: 'Exercise the integration client' },
            async ({ probe, name }: { probe: AspireClientRpc, name: string }) => action(probe, name));
        await runIntegrationHost({
            packageName: 'test-host',
            integrations: [defineIntegration({ name: 'test', capabilities: [capability] })],
        });
        const handler = onRequest.mock.calls.find(([method]) =>
            typeof method === 'object' && method.method === 'handleExternalCapability')![1];
        return (name = '') => handler('test/client', { probe: { $handle: 'probe', $type: 'test/ClientProbe' }, name }, name);
    }

    it('forwards AbortSignal cancellation to the server', async () => {
        const dispatch = await dispatchWithClient(async client => {
            const controller = new AbortController();
            const id = registerCancellation(client, controller.signal);
            controller.abort();
            await Promise.resolve();
            expect(sendRequest).toHaveBeenCalledWith('cancelToken', id);
        });
        await dispatch();
    });

    it('marshals cancellation inputs using the normal transport', async () => {
        const dispatch = await dispatchWithClient(async client => {
            await client.invokeCapability('test/wait', {
                cancellationToken: CancellationToken.fromValue(new AbortController().signal),
            });
            const request = sendRequest.mock.calls.find(([method]) => method === 'invokeCapability')!;
            expect(request[2]).toEqual({ cancellationToken: expect.stringMatching(/^ct_/) });
        });
        await dispatch();
    });

    it('waits for unawaited fluent work before completing an export', async () => {
        let finish!: () => void;
        const work = new Promise<void>(resolve => { finish = resolve; });
        let completed = false;
        const dispatch = await dispatchWithClient(async client => {
            client.trackPromise(work);
            return 'configured';
        });
        const invocation = dispatch().then((result: unknown) => {
            completed = true;
            return result;
        });
        try {
            await new Promise<void>(resolve => setImmediate(resolve));
            expect(completed).toBe(false);
        } finally {
            finish();
        }
        await expect(invocation).resolves.toBe('configured');
    });

    it('reports a failed unawaited fluent call as an export failure', async () => {
        const dispatch = await dispatchWithClient(async client => {
            const work = Promise.reject(new Error('fluent configuration failed'));
            // Keep the broken implementation from crashing the test runner before the assertion.
            void work.catch(() => {});
            client.trackPromise(work);
            return 'configured';
        });
        await expect(dispatch()).rejects.toThrow('One or more unawaited fluent calls failed: fluent configuration failed');
    });

    it('retains both an export failure and a different fluent failure', async () => {
        const dispatch = await dispatchWithClient(async client => {
            client.trackPromise(Promise.reject(new Error('fluent failure')));
            throw new Error('export failure');
        });
        await expect(dispatch()).rejects.toThrow('One or more unawaited fluent calls failed: export failure; fluent failure');
    });

    it('isolates concurrent exports and reentrant callbacks from pending fluent work', async () => {
        let finish!: () => void;
        const work = new Promise<void>(resolve => { finish = resolve; });
        const dispatch = await dispatchWithClient(async (client, name) => {
            if (name === 'blocked') {
                client.trackPromise(work);
            }
            return name;
        });
        const callbackId = registerCallback(() => 'reentrant callback');
        const blocked = dispatch('blocked');
        try {
            await expect(dispatch('independent')).resolves.toBe('independent');
            const callback = onRequest.mock.calls.find(([method]) => method === 'invokeCallback')![1];
            await expect(callback(callbackId, {})).resolves.toBe('reentrant callback');
        } finally {
            finish();
            unregisterCallback(callbackId);
        }
        await expect(blocked).resolves.toBe('blocked');
    });

    it('does not let a failed export poison the next invocation', async () => {
        const dispatch = await dispatchWithClient(async (client, name) => {
            if (name === 'failure') {
                client.trackPromise(Promise.reject(new Error('unawaited failure')));
            }
            return name;
        });
        await expect(dispatch('failure')).rejects.toThrow('One or more unawaited fluent calls failed');
        await expect(dispatch('success')).resolves.toBe('success');
    });

    it('drains fluent work added by a continuation before returning', async () => {
        let finish!: () => void;
        const work = new Promise<void>(resolve => { finish = resolve; });
        let completed = false;
        const dispatch = await dispatchWithClient(async client => {
            client.trackPromise(Promise.resolve().then(() => client.trackPromise(work)));
            return 'configured';
        });
        const invocation = dispatch().then((result: unknown) => {
            completed = true;
            return result;
        });
        try {
            await new Promise<void>(resolve => setImmediate(resolve));
            expect(completed).toBe(false);
        } finally {
            finish();
        }
        await expect(invocation).resolves.toBe('configured');
    });

    it('passes RPC cancellation through an exported cancellation parameter', async () => {
        let cancel!: () => void;
        const dispatch = await dispatchWithClient(async () => 'configured');
        const capability = AspireExport(
            {
                id: 'test/cancel',
                method: 'cancel',
                description: 'Exercise incoming RPC cancellation',
                projection: {
                    returnType: { typeId: 'void', category: 'Primitive' },
                    parameters: [{ name: 'cancellationToken', type: { typeId: 'cancellationToken', category: 'Primitive' } }],
                },
            },
            async ({ cancellationToken, probe }: { cancellationToken: CancellationToken, probe: AspireClientRpc }) => {
                expect(cancellationToken.register(probe)).toMatch(/^ct_/);
                cancel();
                await Promise.resolve();
                expect(sendRequest.mock.calls.some(([method]) => method === 'cancelToken')).toBe(true);
            });
        await runIntegrationHost({
            packageName: 'test-cancel-host',
            integrations: [defineIntegration({ name: 'test', capabilities: [capability] })],
        });
        const handler = onRequest.mock.calls.filter(([method]) =>
            typeof method === 'object' && method.method === 'handleExternalCapability').at(-1)![1];
        const cancellation = {
            isCancellationRequested: false,
            onCancellationRequested: (callback: () => void) => {
                cancel = callback;
                return { dispose: vi.fn() };
            },
        };
        await handler('test/cancel', {
            cancellationToken: 'guest-token',
            probe: { $handle: 'probe', $type: 'test/ClientProbe' },
        }, 'invocation-cancel', cancellation);
        await dispatch();
    });
});
