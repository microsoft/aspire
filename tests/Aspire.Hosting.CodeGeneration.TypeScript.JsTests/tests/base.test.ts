import { describe, it, expect, vi } from 'vitest';
import {
    ReferenceExpression,
    refExpr,
    ResourceBuilderBase,
    AspireList,
    AspireDict,
    Handle,
    AspireClient,
} from '@aspire/base';

// ============================================================================
// Helper: Create a mock AspireClient that records invokeCapability calls
// ============================================================================

interface MockClient extends AspireClient {
    calls: { capabilityId: string; args: Record<string, unknown> | undefined }[];
}

function createMockClient(responses?: Map<string, unknown>): MockClient {
    const calls: { capabilityId: string; args: Record<string, unknown> | undefined }[] = [];
    // Use Object.create so instanceof AspireClient checks pass (needed by registerCancellation).
    // Properties are defined via Object.defineProperty because AspireClient uses getters.
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

function makeHandle(id: string, type: string): Handle {
    return new Handle({ $handle: id, $type: type });
}

// ============================================================================
// ReferenceExpression
// ============================================================================

describe('ReferenceExpression', () => {
    describe('expression mode (format + valueProviders)', () => {
        it('creates from format string and empty providers', () => {
            const expr = new ReferenceExpression('redis://localhost:6379', []);
            expect(expr.isConditional).toBe(false);
            expect(expr.toJSON()).toEqual({
                $expr: { format: 'redis://localhost:6379' },
            });
        });

        it('creates from format string with value providers', () => {
            const handle = makeHandle('1', 'Endpoint');
            const expr = new ReferenceExpression('redis://{0}:6379', [handle.toJSON()]);
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('redis://{0}:6379');
            expect(json.$expr.valueProviders).toEqual([{ $handle: '1', $type: 'Endpoint' }]);
        });

        it('omits valueProviders when array is empty', () => {
            const expr = new ReferenceExpression('literal', []);
            const json = expr.toJSON() as any;
            expect(json.$expr.valueProviders).toBeUndefined();
        });

        it('toString shows the format string', () => {
            const expr = new ReferenceExpression('redis://{0}', []);
            expect(expr.toString()).toBe('ReferenceExpression(redis://{0})');
        });
    });

    describe('handle mode', () => {
        it('delegates toJSON to the handle', () => {
            const handle = makeHandle('42', 'Aspire.Hosting/RefExpr');
            const client = {} as AspireClient;
            const expr = new ReferenceExpression(handle, client);
            expect(expr.toJSON()).toEqual({ $handle: '42', $type: 'Aspire.Hosting/RefExpr' });
        });

        it('toString shows handle mode', () => {
            const handle = makeHandle('1', 'T');
            const expr = new ReferenceExpression(handle, {} as AspireClient);
            expect(expr.toString()).toBe('ReferenceExpression(handle)');
        });

        it('isConditional returns false in handle mode', () => {
            const handle = makeHandle('1', 'T');
            const expr = new ReferenceExpression(handle, {} as AspireClient);
            expect(expr.isConditional).toBe(false);
        });

        it('getValue throws when not in handle mode', async () => {
            const expr = new ReferenceExpression('literal', []);
            await expect(expr.getValue()).rejects.toThrow(
                'getValue is only available on server-returned ReferenceExpression instances'
            );
        });

        it('getValue invokes the capability on the client', async () => {
            const handle = makeHandle('10', 'Aspire.Hosting/RefExpr');
            const responses = new Map([
                ['Aspire.Hosting.ApplicationModel/getValue', 'resolved-value'],
            ]);
            const client = createMockClient(responses);
            const expr = new ReferenceExpression(handle, client);
            const result = await expr.getValue();
            expect(result).toBe('resolved-value');
            expect(client.calls[0].capabilityId).toBe('Aspire.Hosting.ApplicationModel/getValue');
        });
    });

    describe('conditional mode', () => {
        // The constructor routes Handle instances into "handle mode", so
        // conditional mode uses plain marshalled handle objects ({ $handle, $type })
        // as the condition — not Handle class instances.

        it('creates a conditional expression', () => {
            const condition = { $handle: '1', $type: 'Param' };
            const whenTrue = new ReferenceExpression('true-branch', []);
            const whenFalse = new ReferenceExpression('false-branch', []);
            const expr = ReferenceExpression.createConditional(
                condition,
                'True',
                whenTrue,
                whenFalse
            );
            expect(expr.isConditional).toBe(true);
        });

        it('serializes conditional expression correctly', () => {
            const condition = { $handle: '1', $type: 'Param' };
            const whenTrue = new ReferenceExpression('yes:{0}', [makeHandle('2', 'E').toJSON()]);
            const whenFalse = new ReferenceExpression('no', []);
            const expr = ReferenceExpression.createConditional(
                condition,
                'True',
                whenTrue,
                whenFalse
            );

            const json = expr.toJSON() as any;
            expect(json.$expr.condition).toEqual({ $handle: '1', $type: 'Param' });
            expect(json.$expr.matchValue).toBe('True');
            expect(json.$expr.whenTrue.$expr.format).toBe('yes:{0}');
            expect(json.$expr.whenFalse.$expr.format).toBe('no');
        });

        it('toString shows conditional mode', () => {
            const expr = ReferenceExpression.createConditional(
                { $handle: '1', $type: 'P' },
                'True',
                new ReferenceExpression('a', []),
                new ReferenceExpression('b', [])
            );
            expect(expr.toString()).toBe('ReferenceExpression(conditional)');
        });
    });

    describe('create (tagged template)', () => {
        it('creates an expression from a template with no interpolations', () => {
            const expr = ReferenceExpression.create`literal text`;
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('literal text');
            expect(json.$expr.valueProviders).toBeUndefined();
        });

        it('creates an expression with string interpolations', () => {
            const expr = ReferenceExpression.create`prefix-${'hello'}-suffix`;
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('prefix-{0}-suffix');
            expect(json.$expr.valueProviders).toEqual(['hello']);
        });

        it('creates an expression with number interpolations (converted to string)', () => {
            const expr = ReferenceExpression.create`port:${8080}`;
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('port:{0}');
            expect(json.$expr.valueProviders).toEqual(['8080']);
        });

        it('creates an expression with Handle interpolations', () => {
            const handle = makeHandle('5', 'Endpoint');
            const expr = ReferenceExpression.create`redis://${handle}:6379`;
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('redis://{0}:6379');
            expect(json.$expr.valueProviders).toEqual([{ $handle: '5', $type: 'Endpoint' }]);
        });

        it('creates an expression with multiple interpolations', () => {
            const h1 = makeHandle('1', 'Host');
            const h2 = makeHandle('2', 'Port');
            const expr = ReferenceExpression.create`${h1}:${h2}/db`;
            const json = expr.toJSON() as any;
            expect(json.$expr.format).toBe('{0}:{1}/db');
            expect(json.$expr.valueProviders).toHaveLength(2);
        });

        it('throws when interpolating null', () => {
            expect(() => ReferenceExpression.create`test-${null as any}`).toThrow(
                'Cannot use null or undefined in reference expression'
            );
        });

        it('throws when interpolating undefined', () => {
            expect(() => ReferenceExpression.create`test-${undefined as any}`).toThrow(
                'Cannot use null or undefined in reference expression'
            );
        });

        it('throws for non-supported types (boolean)', () => {
            expect(() => ReferenceExpression.create`test-${true as any}`).toThrow(
                'Cannot use value of type boolean in reference expression'
            );
        });
    });

    describe('extractHandleForExpr (tested via create)', () => {
        it('accepts objects with $handle property', () => {
            const marshalledHandle = { $handle: '10', $type: 'X' };
            const expr = ReferenceExpression.create`${marshalledHandle}`;
            const json = expr.toJSON() as any;
            expect(json.$expr.valueProviders[0]).toEqual(marshalledHandle);
        });

        it('accepts objects with $expr property', () => {
            const marshalledExpr = { $expr: { format: 'inner' } };
            const expr = ReferenceExpression.create`${marshalledExpr}`;
            const json = expr.toJSON() as any;
            expect(json.$expr.valueProviders[0]).toBe(marshalledExpr);
        });

        it('accepts objects with toJSON returning $handle', () => {
            const handle = makeHandle('7', 'Wrapper');
            // Handle has toJSON(), so it should work via that path
            const expr = ReferenceExpression.create`${handle}`;
            const json = expr.toJSON() as any;
            expect(json.$expr.valueProviders[0]).toEqual({ $handle: '7', $type: 'Wrapper' });
        });

        it('accepts ReferenceExpression instances via toJSON', () => {
            const inner = new ReferenceExpression('inner-{0}', [makeHandle('1', 'E').toJSON()]);
            // ReferenceExpression has toJSON that returns { $expr: ... }
            const expr = ReferenceExpression.create`outer/${inner}`;
            const json = expr.toJSON() as any;
            expect(json.$expr.valueProviders[0].$expr).toBeDefined();
        });
    });
});

// ============================================================================
// refExpr tagged template
// ============================================================================

describe('refExpr', () => {
    it('works as a tagged template function', () => {
        const handle = makeHandle('1', 'E');
        const expr = refExpr`redis://${handle}:6379`;
        expect(expr).toBeInstanceOf(ReferenceExpression);
        const json = expr.toJSON() as any;
        expect(json.$expr.format).toBe('redis://{0}:6379');
    });

    it('handles plain string template', () => {
        const expr = refExpr`static-value`;
        const json = expr.toJSON() as any;
        expect(json.$expr.format).toBe('static-value');
    });
});

// ============================================================================
// ResourceBuilderBase
// ============================================================================

describe('ResourceBuilderBase', () => {
    it('serializes to handle JSON via toJSON', () => {
        const handle = makeHandle('99', 'Aspire.Hosting/Redis');
        const client = {} as AspireClient;
        const builder = new ResourceBuilderBase(handle, client);
        expect(builder.toJSON()).toEqual({ $handle: '99', $type: 'Aspire.Hosting/Redis' });
    });
});

// ============================================================================
// AspireList
// ============================================================================

describe('AspireList', () => {
    it('count invokes the correct capability', async () => {
        const handle = makeHandle('1', 'List<string>');
        const responses = new Map([['Aspire.Hosting/List.length', 3]]);
        const client = createMockClient(responses);
        const list = new AspireList<string>(handle, client, 'List<string>');
        const count = await list.count();
        expect(count).toBe(3);
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/List.length');
    });

    it('get invokes the correct capability with index', async () => {
        const handle = makeHandle('1', 'List<string>');
        const responses = new Map([['Aspire.Hosting/List.get', 'item-0']]);
        const client = createMockClient(responses);
        const list = new AspireList<string>(handle, client, 'List<string>');
        const item = await list.get(0);
        expect(item).toBe('item-0');
        expect(client.calls[0].args).toEqual({ list: handle, index: 0 });
    });

    it('add invokes the correct capability', async () => {
        const handle = makeHandle('1', 'List<string>');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>');
        await list.add('new-item');
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/List.add');
        expect(client.calls[0].args).toEqual({ list: handle, item: 'new-item' });
    });

    it('removeAt invokes the correct capability', async () => {
        const handle = makeHandle('1', 'List<string>');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>');
        await list.removeAt(2);
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/List.removeAt');
    });

    it('clear invokes the correct capability', async () => {
        const handle = makeHandle('1', 'List<string>');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>');
        await list.clear();
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/List.clear');
    });

    it('toArray invokes the correct capability', async () => {
        const handle = makeHandle('1', 'List<string>');
        const responses = new Map([['Aspire.Hosting/List.toArray', ['a', 'b', 'c']]]);
        const client = createMockClient(responses);
        const list = new AspireList<string>(handle, client, 'List<string>');
        const arr = await list.toArray();
        expect(arr).toEqual(['a', 'b', 'c']);
    });

    it('toJSON works when handle is resolved (no getter capability)', () => {
        const handle = makeHandle('1', 'List<string>');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>');
        expect(list.toJSON()).toEqual({ $handle: '1', $type: 'List<string>' });
    });

    it('toJSON throws when handle is not yet resolved (with getter capability)', () => {
        const handle = makeHandle('1', 'Context');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>', 'getList');
        expect(() => list.toJSON()).toThrow('AspireList must be resolved');
    });

    it('toTransportValue resolves the handle and returns JSON', async () => {
        const handle = makeHandle('1', 'List<string>');
        const client = createMockClient();
        const list = new AspireList<string>(handle, client, 'List<string>');
        const transport = await list.toTransportValue();
        expect(transport).toEqual({ $handle: '1', $type: 'List<string>' });
    });

    it('lazily resolves handle via getter capability', async () => {
        const contextHandle = makeHandle('ctx', 'Context');
        const listHandle = makeHandle('list-1', 'List<string>');
        const responses = new Map<string, unknown>([
            ['getListCap', listHandle],
            ['Aspire.Hosting/List.length', 5],
        ]);
        const client = createMockClient(responses);
        const list = new AspireList<string>(contextHandle, client, 'List<string>', 'getListCap');

        const count = await list.count();
        // First call should be the getter, second should be the count
        expect(client.calls[0].capabilityId).toBe('getListCap');
        expect(client.calls[1].capabilityId).toBe('Aspire.Hosting/List.length');
    });

    it('caches the resolved handle across multiple calls', async () => {
        const contextHandle = makeHandle('ctx', 'Context');
        const listHandle = makeHandle('list-1', 'List<string>');
        const responses = new Map<string, unknown>([
            ['getListCap', listHandle],
            ['Aspire.Hosting/List.length', 5],
        ]);
        const client = createMockClient(responses);
        const list = new AspireList<string>(contextHandle, client, 'List<string>', 'getListCap');

        await list.count();
        await list.count();

        // getListCap should only be called once
        const getterCalls = client.calls.filter(c => c.capabilityId === 'getListCap');
        expect(getterCalls).toHaveLength(1);
    });
});

// ============================================================================
// AspireDict
// ============================================================================

describe('AspireDict', () => {
    it('count invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.count', 2]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const count = await dict.count();
        expect(count).toBe(2);
    });

    it('get invokes the correct capability with key', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.get', 'my-value']]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const value = await dict.get('my-key');
        expect(value).toBe('my-value');
        expect(client.calls[0].args).toEqual({ dict: handle, key: 'my-key' });
    });

    it('set invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const client = createMockClient();
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        await dict.set('key', 'value');
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/Dict.set');
        expect(client.calls[0].args).toEqual({ dict: handle, key: 'key', value: 'value' });
    });

    it('containsKey invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.has', true]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const has = await dict.containsKey('key');
        expect(has).toBe(true);
    });

    it('remove invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.remove', true]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const removed = await dict.remove('key');
        expect(removed).toBe(true);
    });

    it('clear invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const client = createMockClient();
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        await dict.clear();
        expect(client.calls[0].capabilityId).toBe('Aspire.Hosting/Dict.clear');
    });

    it('keys invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.keys', ['a', 'b']]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const keys = await dict.keys();
        expect(keys).toEqual(['a', 'b']);
    });

    it('values invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.values', ['x', 'y']]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const values = await dict.values();
        expect(values).toEqual(['x', 'y']);
    });

    it('toObject invokes the correct capability', async () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const responses = new Map([['Aspire.Hosting/Dict.toObject', { a: '1', b: '2' }]]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        const obj = await dict.toObject();
        expect(obj).toEqual({ a: '1', b: '2' });
    });

    it('toJSON works when handle is resolved (no getter capability)', () => {
        const handle = makeHandle('1', 'Dict<string,string>');
        const client = createMockClient();
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>');
        expect(dict.toJSON()).toEqual({ $handle: '1', $type: 'Dict<string,string>' });
    });

    it('toJSON throws when handle is not yet resolved (with getter capability)', () => {
        const handle = makeHandle('1', 'Context');
        const client = createMockClient();
        const dict = new AspireDict<string, string>(handle, client, 'Dict<string,string>', 'getDict');
        expect(() => dict.toJSON()).toThrow('AspireDict must be resolved');
    });

    it('lazily resolves handle via getter capability', async () => {
        const contextHandle = makeHandle('ctx', 'Context');
        const dictHandle = makeHandle('dict-1', 'Dict<string,string>');
        const responses = new Map<string, unknown>([
            ['getDictCap', dictHandle],
            ['Aspire.Hosting/Dict.count', 3],
        ]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(contextHandle, client, 'Dict<string,string>', 'getDictCap');

        await dict.count();
        expect(client.calls[0].capabilityId).toBe('getDictCap');
        expect(client.calls[1].capabilityId).toBe('Aspire.Hosting/Dict.count');
    });

    it('caches the resolved handle across multiple calls', async () => {
        const contextHandle = makeHandle('ctx', 'Context');
        const dictHandle = makeHandle('dict-1', 'Dict<string,string>');
        const responses = new Map<string, unknown>([
            ['getDictCap', dictHandle],
            ['Aspire.Hosting/Dict.count', 3],
        ]);
        const client = createMockClient(responses);
        const dict = new AspireDict<string, string>(contextHandle, client, 'Dict<string,string>', 'getDictCap');

        await dict.count();
        await dict.count();

        const getterCalls = client.calls.filter(c => c.capabilityId === 'getDictCap');
        expect(getterCalls).toHaveLength(1);
    });
});
