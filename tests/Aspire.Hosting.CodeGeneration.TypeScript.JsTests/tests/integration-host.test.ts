import { EventEmitter } from 'node:events';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AspireExport, defineIntegration } from '@aspire/base';
import { Handle, registerHandleWrapper } from '@aspire/transport';
import { runIntegrationHost } from '../../../playground/TsIntegrationSpike/kafka-integration/host-runtime.js';

const { onRequest, sendRequest } = vi.hoisted(() => ({
    onRequest: vi.fn(),
    sendRequest: vi.fn(),
}));

vi.mock('node:net', () => ({
    createConnection: () => {
        const socket = new EventEmitter();
        queueMicrotask(() => socket.emit('connect'));
        return socket;
    },
}));

vi.mock('vscode-jsonrpc/node.js', () => ({
    RequestType2: class {
        constructor(readonly method: string) {}
    },
    StreamMessageReader: class {},
    StreamMessageWriter: class {},
    createMessageConnection: () => ({
        onRequest,
        sendRequest,
        onError: vi.fn(),
        onClose: vi.fn(),
        listen: vi.fn(),
    }),
}));

describe('integration host callback relay', () => {
    afterEach(() => {
        vi.unstubAllEnvs();
        vi.restoreAllMocks();
        vi.clearAllMocks();
    });

    it.each(['handle', 'nested'] as const)('wraps a %s callback result before invoking integration code', async (shape) => {
        vi.stubEnv('REMOTE_APP_HOST_SOCKET_PATH', 'integration-test-socket');
        vi.stubEnv('ASPIRE_REMOTE_APPHOST_TOKEN', 'integration-test-token');
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
});
