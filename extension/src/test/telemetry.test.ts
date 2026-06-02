import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import * as vscode from 'vscode';
import { __resetCommonPropertiesForTests, __resetTelemetryReporterFactoryForTests, __setReporterForTests, __setTelemetryReporterFactoryForTests, initializeTelemetry, isCommandCancellation, sendTelemetryEvent, setCommandInvocationListener, setCommonTelemetryProperties, withCommandTelemetry } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

interface RecordedErrorEvent extends RecordedEvent {
    isError: true;
}

// A minimal fake TelemetryReporter that just records calls. Mirrors the
// subset of the @vscode/extension-telemetry surface that the extension uses.
class FakeTelemetryReporter {
    public events: (RecordedEvent | RecordedErrorEvent)[] = [];

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true });
    }

    sendDangerousTelemetryEvent(): void { /* not used here */ }
    sendDangerousTelemetryErrorEvent(): void { /* not used here */ }
    sendRawTelemetryEvent(): void { /* not used here */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('telemetry utilities', () => {
    let fake: FakeTelemetryReporter;
    let restore: () => void;

    setup(() => {
        fake = new FakeTelemetryReporter();
        restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        __resetCommonPropertiesForTests();
    });

    teardown(() => {
        setCommandInvocationListener(undefined);
        restore();
        __resetTelemetryReporterFactoryForTests();
        __resetCommonPropertiesForTests();
    });

    test('sendTelemetryEvent merges common properties', () => {
        setCommonTelemetryProperties({ apphost_languages: 'csharp', apphost_present: 'true' });
        sendTelemetryEvent('command/invoked', { command: 'cmd.x' });
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.name, 'command/invoked');
        assert.deepStrictEqual(event.properties, {
            apphost_languages: 'csharp',
            apphost_present: 'true',
            command: 'cmd.x',
        });
    });

    test('setCommonTelemetryProperties replaces and clears keys', () => {
        setCommonTelemetryProperties({ apphost_languages: 'first', apphost_present: 'keep' });
        setCommonTelemetryProperties({ apphost_languages: undefined });
        sendTelemetryEvent('command/invoked', { command: 'cmd.y' });
        assert.deepStrictEqual(fake.events[0].properties, { apphost_present: 'keep', command: 'cmd.y' });
    });

    test('sendTelemetryEvent leaves reporter event names unprefixed because VS Code prefixes extension telemetry', () => {
        sendTelemetryEvent('command/invoked', { command: 'cmd.prefixed' });

        assert.strictEqual(fake.events[0].name, 'command/invoked');
    });

    test('initializeTelemetry does not prefix reporter event names with the extension id', () => {
        restore();
        let createdWithKey: string | undefined;
        const restoreFactory = __setTelemetryReporterFactoryForTests((aiKey) => {
            createdWithKey = aiKey;
            return fake as unknown as TelemetryReporter;
        });

        try {
            const subscriptions: vscode.Disposable[] = [];
            initializeTelemetry({
                extension: {
                    id: 'microsoft-aspire.aspire-vscode',
                    packageJSON: {
                        aiKey: 'test-key'
                    }
                },
                subscriptions
            } as unknown as vscode.ExtensionContext);

            sendTelemetryEvent('command/invoked', { command: 'cmd.initialized' });

            assert.strictEqual(createdWithKey, 'test-key');
            assert.strictEqual(subscriptions.length, 1);
            assert.strictEqual(fake.events[0].name, 'command/invoked');
        }
        finally {
            restoreFactory();
        }
    });

    test('withCommandTelemetry emits success outcome', async () => {
        await withCommandTelemetry('cmd.success', () => 42);
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.name, 'command/invoked');
        assert.strictEqual(event.properties?.command, 'cmd.success');
        assert.strictEqual(event.properties?.outcome, 'success');
        assert.strictEqual(event.properties?.error_kind, undefined);
        assert.ok(typeof event.measurements?.duration_ms === 'number');
    });

    test('withCommandTelemetry includes additional properties', async () => {
        await withCommandTelemetry('cmd.tree', () => undefined, { source: 'tree' });
        assert.strictEqual(fake.events[0].properties?.source, 'tree');
    });

    test('withCommandTelemetry classifies thrown errors and rethrows', async () => {
        await assert.rejects(
            withCommandTelemetry('cmd.error', () => { throw new TypeError('bad'); })
        );
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.properties?.outcome, 'error');
        assert.strictEqual(event.properties?.error_kind, 'TypeError');
    });

    test('withCommandTelemetry classifies cancellations and does not record error_kind', async () => {
        const err = new Error('Canceled');
        err.name = 'Canceled';
        await assert.rejects(withCommandTelemetry('cmd.canceled', () => { throw err; }));
        assert.strictEqual(fake.events[0].properties?.outcome, 'canceled');
        assert.strictEqual(fake.events[0].properties?.error_kind, undefined);
    });

    test('withCommandTelemetry invokes the command invocation listener once per call', async () => {
        let calls = 0;
        setCommandInvocationListener(() => { calls++; });
        await withCommandTelemetry('cmd.a', () => undefined);
        await withCommandTelemetry('cmd.b', () => undefined);
        await withCommandTelemetry('cmd.c', () => undefined);
        assert.strictEqual(calls, 3);
    });

    test('isCommandCancellation recognizes the well-known cancellation shapes', () => {
        const e1 = new Error('Canceled');
        e1.name = 'Canceled';
        assert.strictEqual(isCommandCancellation(e1), true);

        const e2 = new Error('CancellationError thrown');
        e2.name = 'CancellationError';
        assert.strictEqual(isCommandCancellation(e2), true);

        const e3 = new Error('canceled');
        assert.strictEqual(isCommandCancellation(e3), true);

        assert.strictEqual(isCommandCancellation('Canceled'), true);
        assert.strictEqual(isCommandCancellation(new Error('something else')), false);
        assert.strictEqual(isCommandCancellation(undefined), false);
    });
});
