import { describe, it, expect, vi } from 'vitest';
import {
    AspireClient,
    Handle,
} from '@aspire/transport';

interface MockClient extends AspireClient {
    calls: { capabilityId: string; args: Record<string, unknown> | undefined }[];
}

function createMockClient(responses?: Map<string, unknown>): MockClient {
    const calls: { capabilityId: string; args: Record<string, unknown> | undefined }[] = [];
    const client = Object.create(AspireClient.prototype);
    Object.defineProperty(client, 'calls', { value: calls, writable: true });
    Object.defineProperty(client, 'connected', { get: () => true });
    Object.defineProperty(client, 'invokeCapability', {
        value: vi.fn(async (capabilityId: string, args?: Record<string, unknown>) => {
            calls.push({ capabilityId, args });
            return responses?.get(capabilityId);
        }),
    });
    return client as MockClient;
}

class NullableScalarContext {
    private readonly _handle: Handle;
    private readonly _client: MockClient;

    public constructor(handle: Handle, client: MockClient) {
        this._handle = handle;
        this._client = client;
    }

    public name = {
        get: async (): Promise<string> => {
            return await this._client.invokeCapability<string>(
                'Test.NullableScalarContext.name',
                { context: this._handle }
            );
        },
        set: async (value: string): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Test.NullableScalarContext.setName',
                { context: this._handle, value }
            );
        },
    };

    public description = {
        get: async (): Promise<string | null> => {
            return await this._client.invokeCapability<string | null>(
                'Test.NullableScalarContext.description',
                { context: this._handle }
            );
        },
        set: async (value: string | null): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Test.NullableScalarContext.setDescription',
                { context: this._handle, value }
            );
        },
    };
}

function createContext(responses?: Map<string, unknown>) {
    const client = createMockClient(responses);
    const handle = new Handle({ $handle: 'context-1', $type: 'Test.NullableScalarContext' });
    return { client, context: new NullableScalarContext(handle, client), handle };
}

describe('generated nullable scalar property accessors', () => {
    it('returns null from nullable scalar getters', async () => {
        const { context, client, handle } = createContext(new Map([
            ['Test.NullableScalarContext.description', null],
        ]));

        await expect(context.description.get()).resolves.toBeNull();
        expect(client.calls).toEqual([
            {
                capabilityId: 'Test.NullableScalarContext.description',
                args: { context: handle },
            },
        ]);
    });

    it('sends null through nullable scalar setters', async () => {
        const { context, client, handle } = createContext();

        await context.description.set(null);

        expect(client.calls).toEqual([
            {
                capabilityId: 'Test.NullableScalarContext.setDescription',
                args: { context: handle, value: null },
            },
        ]);
    });

    it('keeps non-null scalar getter and setter behavior unchanged', async () => {
        const { context, client, handle } = createContext(new Map([
            ['Test.NullableScalarContext.name', 'cache'],
        ]));

        await expect(context.name.get()).resolves.toBe('cache');
        await context.name.set('api');

        expect(client.calls).toEqual([
            {
                capabilityId: 'Test.NullableScalarContext.name',
                args: { context: handle },
            },
            {
                capabilityId: 'Test.NullableScalarContext.setName',
                args: { context: handle, value: 'api' },
            },
        ]);
    });
});
