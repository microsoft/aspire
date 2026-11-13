import * as assert from 'assert';
import { ensureBlazorWasmDebuggerReady } from '../testing/blazorWasmDebuggerSetup';

suite('Blazor WASM debugger setup', () => {
    test('does not reload when the first activation is ready', async () => {
        const calls: string[] = [];
        await ensureBlazorWasmDebuggerReady({
            prepare: async () => { calls.push('prepare'); return 'ready'; },
            reloadWindow: async () => { calls.push('reload'); },
            onRetry: attempt => { calls.push(`retry-${attempt}`); },
        });

        assert.deepStrictEqual(calls, ['prepare']);
    });

    test('reacquires only missing bridge dependencies before allowing the proof to start', async () => {
        const calls: string[] = [];
        let preparations = 0;
        await ensureBlazorWasmDebuggerReady({
            prepare: async () => {
                calls.push('prepare');
                return ++preparations === 3 ? 'ready' : 'missing-bridge';
            },
            reloadWindow: async () => { calls.push('reload'); },
            onRetry: attempt => { calls.push(`retry-${attempt}`); },
        });
        calls.push('proof');

        assert.deepStrictEqual(calls, [
            'prepare', 'retry-1', 'reload',
            'prepare', 'retry-2', 'reload',
            'prepare', 'proof',
        ]);
    });

    test('fails explicitly after bounded missing-component attempts', async () => {
        const calls: string[] = [];
        await assert.rejects(ensureBlazorWasmDebuggerReady({
            prepare: async () => { calls.push('prepare'); return 'missing-bridge'; },
            reloadWindow: async () => { calls.push('reload'); },
            onRetry: attempt => { calls.push(`retry-${attempt}`); },
        }), /VSWebAssemblyBridge is still missing after 3 C# activations/);

        assert.deepStrictEqual(calls, [
            'prepare', 'retry-1', 'reload',
            'prepare', 'retry-2', 'reload',
            'prepare',
        ]);
    });

    for (const status of [undefined, null, false, {}, 'legacy-proxy']) {
        test(`rejects an unexpected readiness response ${JSON.stringify(status)}`, async () => {
            await assert.rejects(ensureBlazorWasmDebuggerReady({
                prepare: async () => status,
                reloadWindow: async () => { assert.fail('Unexpected responses must not trigger reload.'); },
                onRetry: () => { assert.fail('Unexpected responses must not be retried.'); },
            }), /unexpected readiness response/);
        });
    }

    test('does not retry preparation errors', async () => {
        const error = new Error('C# activation failed');
        await assert.rejects(ensureBlazorWasmDebuggerReady({
            prepare: async () => { throw error; },
            reloadWindow: async () => { assert.fail('Preparation failures must not trigger reload.'); },
            onRetry: () => { assert.fail('Preparation failures must not be retried.'); },
        }), caught => caught === error);
    });

    test('preserves a reload failure without trying another activation', async () => {
        const error = new Error('Extension host reload failed');
        const calls: string[] = [];
        await assert.rejects(ensureBlazorWasmDebuggerReady({
            prepare: async () => { calls.push('prepare'); return 'missing-bridge'; },
            reloadWindow: async () => { calls.push('reload'); throw error; },
            onRetry: attempt => { calls.push(`retry-${attempt}`); },
        }), caught => caught === error);

        assert.deepStrictEqual(calls, ['prepare', 'retry-1', 'reload']);
    });
});
