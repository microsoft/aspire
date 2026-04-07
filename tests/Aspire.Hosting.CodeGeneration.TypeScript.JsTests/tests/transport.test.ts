import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as net from 'net';
import * as rpc from 'vscode-jsonrpc/node.js';
import {
    isAtsError,
    isMarshalledHandle,
    Handle,
    CancellationToken,
    registerCallback,
    unregisterCallback,
    getCallbackCount,
    wrapIfHandle,
    registerHandleWrapper,
    CapabilityError,
    AppHostUsageError,
    AtsErrorCodes,
    AspireClient,
    registerCancellation,
    unregisterCancellation,
} from '@aspire/transport';

// ============================================================================
// Type Guards
// ============================================================================

describe('isAtsError', () => {
    it('returns true for a valid ATS error object', () => {
        const error = { $error: { code: 'INTERNAL_ERROR', message: 'something broke' } };
        expect(isAtsError(error)).toBe(true);
    });

    it('returns false for null', () => {
        expect(isAtsError(null)).toBe(false);
    });

    it('returns false for undefined', () => {
        expect(isAtsError(undefined)).toBe(false);
    });

    it('returns false for a primitive', () => {
        expect(isAtsError('string')).toBe(false);
        expect(isAtsError(42)).toBe(false);
    });

    it('returns false for an object without $error', () => {
        expect(isAtsError({ code: 'INTERNAL_ERROR' })).toBe(false);
    });

    it('returns false when $error is not an object', () => {
        expect(isAtsError({ $error: 'not-an-object' })).toBe(false);
    });

    it('returns false for $error: null', () => {
        // typeof null === 'object' in JavaScript, but null is not a valid ATS error.
        expect(isAtsError({ $error: null })).toBe(false);
    });
});

describe('isMarshalledHandle', () => {
    it('returns true for a valid marshalled handle', () => {
        expect(isMarshalledHandle({ $handle: '1', $type: 'SomeType' })).toBe(true);
    });

    it('returns false for null', () => {
        expect(isMarshalledHandle(null)).toBe(false);
    });

    it('returns false for undefined', () => {
        expect(isMarshalledHandle(undefined)).toBe(false);
    });

    it('returns false for an object missing $handle', () => {
        expect(isMarshalledHandle({ $type: 'SomeType' })).toBe(false);
    });

    it('returns false for an object missing $type', () => {
        expect(isMarshalledHandle({ $handle: '1' })).toBe(false);
    });

    it('returns false for a primitive', () => {
        expect(isMarshalledHandle('string')).toBe(false);
    });

    it('accepts extra properties alongside $handle and $type', () => {
        expect(isMarshalledHandle({ $handle: '1', $type: 'T', extra: 'data' })).toBe(true);
    });
});

// ============================================================================
// AtsErrorCodes
// ============================================================================

describe('AtsErrorCodes', () => {
    it('has expected error code values', () => {
        expect(AtsErrorCodes.CapabilityNotFound).toBe('CAPABILITY_NOT_FOUND');
        expect(AtsErrorCodes.HandleNotFound).toBe('HANDLE_NOT_FOUND');
        expect(AtsErrorCodes.TypeMismatch).toBe('TYPE_MISMATCH');
        expect(AtsErrorCodes.InvalidArgument).toBe('INVALID_ARGUMENT');
        expect(AtsErrorCodes.ArgumentOutOfRange).toBe('ARGUMENT_OUT_OF_RANGE');
        expect(AtsErrorCodes.CallbackError).toBe('CALLBACK_ERROR');
        expect(AtsErrorCodes.InternalError).toBe('INTERNAL_ERROR');
    });
});

// ============================================================================
// Handle
// ============================================================================

describe('Handle', () => {
    it('stores handle ID and type ID from marshalled input', () => {
        const handle = new Handle({ $handle: '42', $type: 'Aspire.Hosting/Builder' });
        expect(handle.$handle).toBe('42');
        expect(handle.$type).toBe('Aspire.Hosting/Builder');
    });

    it('serializes to JSON matching the marshalled format', () => {
        const handle = new Handle({ $handle: '7', $type: 'Aspire.Hosting/Redis' });
        expect(handle.toJSON()).toEqual({ $handle: '7', $type: 'Aspire.Hosting/Redis' });
    });

    it('provides a debug-friendly toString', () => {
        const handle = new Handle({ $handle: '3', $type: 'Aspire.Hosting/Endpoint' });
        expect(handle.toString()).toBe('Handle<Aspire.Hosting/Endpoint>(3)');
    });

    it('roundtrips through JSON serialization', () => {
        const original = new Handle({ $handle: '99', $type: 'MyType' });
        const json = JSON.parse(JSON.stringify(original));
        expect(isMarshalledHandle(json)).toBe(true);
        const restored = new Handle(json);
        expect(restored.$handle).toBe('99');
        expect(restored.$type).toBe('MyType');
    });
});

// ============================================================================
// CancellationToken
// ============================================================================

describe('CancellationToken', () => {
    it('constructs from a remote token ID string', () => {
        const token = new CancellationToken('remote-id-123');
        expect(token.toJSON()).toBe('remote-id-123');
    });

    it('constructs from an AbortSignal without a remote ID', () => {
        const controller = new AbortController();
        const token = new CancellationToken(controller.signal);
        expect(token.toJSON()).toBeUndefined();
    });

    it('constructs with no arguments', () => {
        const token = new CancellationToken();
        expect(token.toJSON()).toBeUndefined();
    });

    it('constructs with null argument', () => {
        const token = new CancellationToken(null as any);
        expect(token.toJSON()).toBeUndefined();
    });

    describe('from', () => {
        it('creates a token from an AbortSignal', () => {
            const controller = new AbortController();
            const token = CancellationToken.from(controller.signal);
            expect(token).toBeInstanceOf(CancellationToken);
            expect(token.toJSON()).toBeUndefined();
        });

        it('creates a token with no signal', () => {
            const token = CancellationToken.from();
            expect(token).toBeInstanceOf(CancellationToken);
        });
    });

    describe('fromValue', () => {
        it('returns the same instance if already a CancellationToken', () => {
            const original = new CancellationToken('id-1');
            const result = CancellationToken.fromValue(original);
            expect(result).toBe(original);
        });

        it('creates a token from a string', () => {
            const result = CancellationToken.fromValue('remote-42');
            expect(result.toJSON()).toBe('remote-42');
        });

        it('creates a token from an AbortSignal', () => {
            const controller = new AbortController();
            const result = CancellationToken.fromValue(controller.signal);
            expect(result).toBeInstanceOf(CancellationToken);
            expect(result.toJSON()).toBeUndefined();
        });

        it('creates an empty token for unsupported values', () => {
            const result = CancellationToken.fromValue(42);
            expect(result).toBeInstanceOf(CancellationToken);
            expect(result.toJSON()).toBeUndefined();
        });

        it('creates an empty token for null', () => {
            const result = CancellationToken.fromValue(null);
            expect(result).toBeInstanceOf(CancellationToken);
        });
    });

    describe('register', () => {
        it('returns the remote token ID when constructed with a string', () => {
            const token = new CancellationToken('remote-99');
            expect(token.register()).toBe('remote-99');
        });
    });
});

// ============================================================================
// wrapIfHandle
// ============================================================================

describe('wrapIfHandle', () => {
    it('wraps a marshalled handle into a Handle instance', () => {
        const result = wrapIfHandle({ $handle: '1', $type: 'T' });
        expect(result).toBeInstanceOf(Handle);
        expect((result as Handle).$handle).toBe('1');
        expect((result as Handle).$type).toBe('T');
    });

    it('returns primitives unchanged', () => {
        expect(wrapIfHandle('hello')).toBe('hello');
        expect(wrapIfHandle(42)).toBe(42);
        expect(wrapIfHandle(true)).toBe(true);
        expect(wrapIfHandle(null)).toBeNull();
        expect(wrapIfHandle(undefined)).toBeUndefined();
    });

    it('wraps handles nested in arrays', () => {
        const arr = [
            { $handle: '1', $type: 'A' },
            'plain',
            { $handle: '2', $type: 'B' },
        ];
        const result = wrapIfHandle(arr) as unknown[];
        expect(result[0]).toBeInstanceOf(Handle);
        expect(result[1]).toBe('plain');
        expect(result[2]).toBeInstanceOf(Handle);
    });

    it('wraps handles nested in plain objects', () => {
        const obj = {
            nested: { $handle: '5', $type: 'Inner' },
            value: 'hello',
        };
        const result = wrapIfHandle(obj) as Record<string, unknown>;
        expect(result.nested).toBeInstanceOf(Handle);
        expect((result.nested as Handle).$handle).toBe('5');
        expect(result.value).toBe('hello');
    });

    it('wraps handles deeply nested in mixed structures', () => {
        const deep = {
            level1: {
                level2: [
                    { $handle: '10', $type: 'Deep' },
                ],
            },
        };
        const result = wrapIfHandle(deep) as any;
        expect(result.level1.level2[0]).toBeInstanceOf(Handle);
    });

    it('uses registered wrapper factory when client is provided', () => {
        const mockClient = {} as AspireClient;
        const wrapper = { custom: true };

        // Note: registerHandleWrapper has no unregister counterpart, so this
        // registration leaks into the module-level registry for the process lifetime.
        registerHandleWrapper('TestType', (_handle, _client) => wrapper);

        const result = wrapIfHandle({ $handle: '1', $type: 'TestType' }, mockClient);
        expect(result).toBe(wrapper);
    });

    it('returns plain Handle when no wrapper is registered', () => {
        const mockClient = {} as AspireClient;
        const result = wrapIfHandle({ $handle: '1', $type: 'UnregisteredType' }, mockClient);
        expect(result).toBeInstanceOf(Handle);
    });

    it('handles non-plain objects (class instances) by returning them as-is', () => {
        class Custom {
            value = 42;
        }
        const instance = new Custom();
        const result = wrapIfHandle(instance);
        expect(result).toBe(instance);
    });
});

// ============================================================================
// CapabilityError
// ============================================================================

describe('CapabilityError', () => {
    it('extends Error and has correct name', () => {
        const error = new CapabilityError({
            code: 'INTERNAL_ERROR',
            message: 'something went wrong',
        });
        expect(error).toBeInstanceOf(Error);
        expect(error.name).toBe('CapabilityError');
    });

    it('exposes error code and message', () => {
        const error = new CapabilityError({
            code: 'CAPABILITY_NOT_FOUND',
            message: 'cap not found',
            capability: 'Aspire.Hosting/test',
        });
        expect(error.code).toBe('CAPABILITY_NOT_FOUND');
        expect(error.message).toBe('cap not found');
        expect(error.capability).toBe('Aspire.Hosting/test');
    });

    it('exposes the full error object', () => {
        const atsError = {
            code: 'TYPE_MISMATCH',
            message: 'wrong type',
            details: { parameter: 'ctx', expected: 'Builder', actual: 'Redis' },
        };
        const error = new CapabilityError(atsError);
        expect(error.error).toBe(atsError);
        expect(error.error.details?.parameter).toBe('ctx');
    });
});

// ============================================================================
// Callback Registry
// ============================================================================

describe('registerCallback / unregisterCallback / getCallbackCount', () => {
    let initialCount: number;
    const registeredIds: string[] = [];

    beforeEach(() => {
        initialCount = getCallbackCount();
    });

    afterEach(() => {
        for (const id of registeredIds) {
            unregisterCallback(id);
        }
        registeredIds.length = 0;
    });

    it('registers a callback and returns a unique ID', () => {
        const id1 = registerCallback(() => {});
        const id2 = registerCallback(() => {});
        registeredIds.push(id1, id2);
        expect(id1).not.toBe(id2);
        expect(id1).toMatch(/^callback_\d+_\d+$/);
        expect(getCallbackCount()).toBe(initialCount + 2);
    });

    it('unregisters a callback by ID', () => {
        const id = registerCallback(() => {});
        expect(getCallbackCount()).toBe(initialCount + 1);

        const removed = unregisterCallback(id);
        expect(removed).toBe(true);
        expect(getCallbackCount()).toBe(initialCount);
    });

    it('returns false when unregistering a nonexistent callback', () => {
        expect(unregisterCallback('nonexistent')).toBe(false);
    });
});

// ============================================================================
// validateCapabilityArgs (tested indirectly via AspireClient.invokeCapability)
// ============================================================================

describe('AppHostUsageError', () => {
    it('extends Error with the correct name', () => {
        const err = new AppHostUsageError('forgot await');
        expect(err).toBeInstanceOf(Error);
        expect(err.name).toBe('AppHostUsageError');
        expect(err.message).toBe('forgot await');
    });
});

// ============================================================================
// Circular reference handling in validateCapabilityArgs
// ============================================================================

describe('circular reference detection in wrapIfHandle', () => {
    // wrapIfHandle does NOT have a visited-set guard (unlike validateCapabilityArgs).
    // Circular plain objects cause a stack overflow. These tests document that behavior
    // so that if a fix is added later, the tests can be updated to assert success.

    it('stack-overflows on self-referencing plain objects', () => {
        const obj: Record<string, unknown> = { a: 1 };
        obj.self = obj;
        expect(() => wrapIfHandle(obj)).toThrow(RangeError);
    });

    it('stack-overflows on arrays with circular references', () => {
        const arr: unknown[] = [1, 2];
        arr.push(arr);
        expect(() => wrapIfHandle(arr)).toThrow(RangeError);
    });

    it('does not overflow on non-plain class instances with circular refs', () => {
        // Class instances are not "plain objects" so wrapIfHandle returns them as-is
        class Node { self: Node; constructor() { this.self = this; } }
        const node = new Node();
        expect(wrapIfHandle(node)).toBe(node);
    });
});

// ============================================================================
// AspireClient
// ============================================================================

describe('AspireClient', () => {
    it('starts in disconnected state', () => {
        const client = new AspireClient('test-pipe');
        expect(client.connected).toBe(false);
    });

    it('rejects ping when not connected', async () => {
        const client = new AspireClient('test-pipe');
        await expect(client.ping()).rejects.toThrow('Not connected');
    });

    it('rejects cancelToken when not connected', async () => {
        const client = new AspireClient('test-pipe');
        await expect(client.cancelToken('token-1')).rejects.toThrow('Not connected');
    });

    it('rejects invokeCapability when not connected', async () => {
        const client = new AspireClient('no-pipe');
        await expect(client.invokeCapability('test/cap')).rejects.toThrow('Not connected');
    });

    it('disconnect is safe to call when not connected', () => {
        const client = new AspireClient('test-pipe');
        expect(() => client.disconnect()).not.toThrow();
    });

    it('disconnect is idempotent', () => {
        const client = new AspireClient('test-pipe');
        client.disconnect();
        client.disconnect();
        expect(client.connected).toBe(false);
    });

    it('registers disconnect callbacks', () => {
        const client = new AspireClient('test-pipe');
        const callback = vi.fn();
        client.onDisconnect(callback);
        // Callback should not be called until disconnect actually happens
        expect(callback).not.toHaveBeenCalled();
    });

    it('connect rejects with timeout on nonexistent pipe', async () => {
        const client = new AspireClient('nonexistent-pipe-' + Date.now());
        await expect(client.connect(500)).rejects.toThrow();
    });

    it('sends the authentication token as a direct parameter during connect', async () => {
        const previousToken = process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
        const authenticate = vi.fn(async (token: string) => {
            expect(token).toBe('secret-token');
            return true;
        });

        process.env.ASPIRE_REMOTE_APPHOST_TOKEN = 'secret-token';

        const fixture = createPipeFixture({ authenticate });
        await fixture.start();

        const client = new AspireClient(fixture.clientSocketPath);

        try {
            await client.connect();
            await fixture.waitForClient();

            expect(authenticate).toHaveBeenCalledOnce();
        } finally {
            if (previousToken === undefined) {
                delete process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
            } else {
                process.env.ASPIRE_REMOTE_APPHOST_TOKEN = previousToken;
            }

            await fixture.cleanup(client);
        }
    });
});

// ============================================================================
// registerCancellation / unregisterCancellation
// ============================================================================

describe('registerCancellation', () => {
    it('returns undefined when no signal is provided', () => {
        expect(registerCancellation(undefined)).toBeUndefined();
    });

    it('returns the remote token ID for a CancellationToken with remote ID', () => {
        const token = new CancellationToken('remote-42');
        const result = registerCancellation(token);
        expect(result).toBe('remote-42');
    });

    it('unregisterCancellation is a no-op for undefined', () => {
        expect(() => unregisterCancellation(undefined)).not.toThrow();
    });

    it('unregisterCancellation is a no-op for unknown IDs', () => {
        expect(() => unregisterCancellation('unknown-id')).not.toThrow();
    });
});

// ============================================================================
// registerHandleWrapper
// ============================================================================

// Note: registerHandleWrapper has no unregister counterpart, so factory
// registrations below persist in the module-level registry for the process
// lifetime. This is acceptable because vitest runs each file in isolation.
describe('registerHandleWrapper', () => {
    it('registered factory is used by wrapIfHandle', () => {
        const mockClient = {} as AspireClient;
        const customObj = { wrapped: true, typeId: 'custom' };

        registerHandleWrapper('CustomWrapperType', (handle, client) => ({
            ...customObj,
            handleId: handle.$handle,
        }));

        const result = wrapIfHandle(
            { $handle: '55', $type: 'CustomWrapperType' },
            mockClient
        ) as any;

        expect(result.wrapped).toBe(true);
        expect(result.handleId).toBe('55');
    });

    it('overwrites a previously registered factory for the same type', () => {
        const mockClient = {} as AspireClient;

        registerHandleWrapper('OverwriteType', () => ({ version: 1 }));
        registerHandleWrapper('OverwriteType', () => ({ version: 2 }));

        const result = wrapIfHandle(
            { $handle: '1', $type: 'OverwriteType' },
            mockClient
        ) as any;

        expect(result.version).toBe(2);
    });
});

// ============================================================================
// Callback Invocation Protocol (via real named pipe)
// ============================================================================

/**
 * Creates a named-pipe server that speaks JSON-RPC, connects an AspireClient,
 * and provides a `sendRequest` helper to invoke callbacks on the client side.
 */
function createPipeFixture(options?: { authenticate?: (token: string) => boolean | Promise<boolean> }) {
    const pipeName = `aspire-test-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const pipePath = process.platform === 'win32' ? `\\\\.\\pipe\\${pipeName}` : `/tmp/${pipeName}`;
    let serverConnection: rpc.MessageConnection | null = null;
    let serverSocket: net.Socket | null = null;
    let onClientConnected: (() => void) | null = null;
    const clientConnectedPromise = new Promise<void>((resolve) => { onClientConnected = resolve; });

    const server = net.createServer((socket) => {
        serverSocket = socket;
        const reader = new rpc.SocketMessageReader(socket);
        const writer = new rpc.SocketMessageWriter(socket);
        serverConnection = rpc.createMessageConnection(reader, writer);

        // Handle ping (required by AspireClient)
        serverConnection.onRequest('ping', () => 'pong');

        // Handle invokeCapability (return empty for most tests)
        serverConnection.onRequest('invokeCapability', () => null);

        // Handle cancelToken
        serverConnection.onRequest('cancelToken', () => true);

        if (options?.authenticate) {
            serverConnection.onRequest('authenticate', options.authenticate);
        }

        serverConnection.listen();
        onClientConnected?.();
    });

    // On Windows, AspireClient prepends \\.\pipe\ so pass just the name.
    // On Linux, AspireClient uses the path as-is so pass the full /tmp/ path.
    const clientSocketPath = process.platform === 'win32' ? pipeName : pipePath;

    return {
        pipeName,
        clientSocketPath,
        start: () => new Promise<void>((resolve) => server.listen(pipePath, resolve)),
        waitForClient: () => clientConnectedPromise,
        invokeCallback: async (callbackId: string, args: unknown): Promise<unknown> => {
            if (!serverConnection) throw new Error('No client connected');
            return serverConnection.sendRequest('invokeCallback', callbackId, args);
        },
        cleanup: async (client: AspireClient) => {
            client.disconnect();
            serverConnection?.dispose();
            serverSocket?.destroy();
            await new Promise<void>((resolve) => server.close(() => resolve()));
        },
    };
}

describe('callback invocation protocol', () => {
    async function connectFixture() {
        const previousToken = process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
        const testToken = 'callback-test-token';
        process.env.ASPIRE_REMOTE_APPHOST_TOKEN = testToken;

        const fixture = createPipeFixture({
            authenticate: (token: string) => token === testToken,
        });

        await fixture.start();
        const client = new AspireClient(fixture.clientSocketPath);

        try {
            await client.connect();
            await fixture.waitForClient();
        }
        catch (error)
        {
            if (previousToken === undefined) {
                delete process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
            } else {
                process.env.ASPIRE_REMOTE_APPHOST_TOKEN = previousToken;
            }

            await fixture.cleanup(client);
            throw error;
        }

        return {
            fixture: {
                ...fixture,
                cleanup: async (client: AspireClient) => {
                    if (previousToken === undefined) {
                        delete process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
                    } else {
                        process.env.ASPIRE_REMOTE_APPHOST_TOKEN = previousToken;
                    }

                    await fixture.cleanup(client);
                },
            },
            client,
        };
    }

    it('invokes a no-arg callback', async () => {
        const { fixture, client } = await connectFixture();
        const fn = vi.fn();
        const id = registerCallback(fn);

        try {
            await fixture.invokeCallback(id, null);
            expect(fn).toHaveBeenCalledOnce();
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });

    it('unpacks positional arguments (p0, p1, ...)', async () => {
        const { fixture, client } = await connectFixture();
        const fn = vi.fn((_a: unknown, _b: unknown) => {});
        const id = registerCallback(fn);

        try {
            await fixture.invokeCallback(id, { p0: 'hello', p1: 42 });
            expect(fn).toHaveBeenCalledWith('hello', 42);
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });

    it('wraps marshalled handles in positional arguments', async () => {
        const { fixture, client } = await connectFixture();
        let receivedArg: unknown;
        const fn = vi.fn((arg: unknown) => { receivedArg = arg; });
        const id = registerCallback(fn);

        try {
            await fixture.invokeCallback(id, { p0: { $handle: '99', $type: 'MyResource' } });
            expect(fn).toHaveBeenCalledOnce();
            expect(receivedArg).toBeInstanceOf(Handle);
            expect((receivedArg as Handle).$handle).toBe('99');
            expect((receivedArg as Handle).$type).toBe('MyResource');
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });

    it('returns the callback result for non-void callbacks', async () => {
        const { fixture, client } = await connectFixture();
        const id = registerCallback((_a: unknown, _b: unknown) => 'computed-result');

        try {
            const result = await fixture.invokeCallback(id, { p0: 'x', p1: 'y' });
            expect(result).toBe('computed-result');
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });

    it('DTO writeback: returns original args when callback returns undefined', async () => {
        const { fixture, client } = await connectFixture();

        // Simulates a void callback that mutates a DTO property
        const id = registerCallback((dto: any) => {
            dto.name = 'mutated';
            // returns undefined (void)
        });

        try {
            const result = await fixture.invokeCallback(id, { p0: { name: 'original' } });
            // The writeback protocol returns the original args object (with mutations)
            const resultObj = result as Record<string, unknown>;
            expect(resultObj.p0).toBeDefined();
            expect((resultObj.p0 as any).name).toBe('mutated');
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });

    it('rejects when callback ID is not registered', async () => {
        const { fixture, client } = await connectFixture();

        try {
            await expect(fixture.invokeCallback('nonexistent-id', null))
                .rejects.toThrow();
        } finally {
            await fixture.cleanup(client);
        }
    });

    it('rejects when callback throws an error', async () => {
        const { fixture, client } = await connectFixture();
        const id = registerCallback(() => { throw new Error('boom'); });

        try {
            await expect(fixture.invokeCallback(id, null))
                .rejects.toThrow();
        } finally {
            unregisterCallback(id);
            await fixture.cleanup(client);
        }
    });
});
