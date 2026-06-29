import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import * as vscode from 'vscode';
import { __resetCommonPropertiesForTests, __resetTelemetryReporterFactoryForTests, __setReporterForTests, __setTelemetryReporterFactoryForTests, initializeTelemetry, isCommandCancellation, sendTelemetryErrorEvent, sendTelemetryEvent, setCommandInvocationListener, setCommonTelemetryProperties, withCommandTelemetry } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
    isError?: boolean;
    isDangerous?: boolean;
}

type TelemetryLevel = 'all' | 'error' | 'crash' | 'off';

// A minimal fake TelemetryReporter that records calls and exposes
// `telemetryLevel`. The extension routes telemetry through
// `sendDangerousTelemetryEvent` / `sendDangerousTelemetryErrorEvent` so the
// VS Code `TelemetryLogger`-applied `<extensionId>/` prefix is bypassed; the
// regular `sendTelemetryEvent` / `sendTelemetryErrorEvent` methods are kept
// here only to fail loudly if the extension ever silently regresses back to
// the prefixed path.
class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];
    public telemetryLevel: TelemetryLevel = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isDangerous: false });
    }

    sendTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true, isDangerous: false });
    }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isDangerous: true });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true, isDangerous: true });
    }

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
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.x' });
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.name, 'aspire/vscode/command/invoked');
        assert.deepStrictEqual(event.properties, {
            apphost_languages: 'csharp',
            apphost_present: 'true',
            command: 'cmd.x',
        });
    });

    test('setCommonTelemetryProperties replaces and clears keys', () => {
        setCommonTelemetryProperties({ apphost_languages: 'first', apphost_present: 'keep' });
        setCommonTelemetryProperties({ apphost_languages: undefined });
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.y' });
        assert.deepStrictEqual(fake.events[0].properties, { apphost_present: 'keep', command: 'cmd.y' });
    });

    test('sendTelemetryEvent emits via the dangerous channel so VS Code does not add an extension-id prefix', () => {
        // `vscode.env.createTelemetryLogger` (used internally by
        // `@vscode/extension-telemetry`'s regular `sendTelemetryEvent`) prepends
        // `<extensionId>/` to every event name, turning
        // `aspire/vscode/command/invoked` into
        // `microsoft-aspire.aspire-vscode/aspire/vscode/command/invoked` on
        // the wire. `sendDangerousTelemetryEvent` skips the logger and reaches
        // the sender directly, preserving the registry-declared name verbatim.
        // This test pins the dangerous path so a future refactor cannot
        // accidentally regress back to the prefixed channel.
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.prefixed' });

        assert.strictEqual(fake.events.length, 1);
        assert.strictEqual(fake.events[0].name, 'aspire/vscode/command/invoked');
        assert.strictEqual(fake.events[0].isDangerous, true, 'must route through sendDangerousTelemetryEvent');
        assert.notStrictEqual(fake.events[0].name.startsWith('microsoft-aspire.aspire-vscode/'), true, 'wire name must not include extension-id prefix');
    });

    test('sendTelemetryErrorEvent emits via the dangerous error channel', () => {
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        }, { duration_ms: 12 });

        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.name, 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(event.isError, true);
        assert.strictEqual(event.isDangerous, true, 'must route through sendDangerousTelemetryErrorEvent');
    });

    test('dashboard passthrough event names emit verbatim through the dangerous channel', () => {
        // Sanity-check that an `aspire/dashboard/*` registry entry reaches the
        // wire as-is. The passthrough is the only producer that uses this
        // namespace; the prefix bypass is what lets the dashboard's native
        // `aspire/dashboard/...` names survive intact (instead of becoming
        // `microsoft-aspire.aspire-vscode/aspire/dashboard/...`).
        sendTelemetryEvent('aspire/dashboard/operation', {
            dashboard_event_name: 'aspire/dashboard/command',
            result: 'success',
        });

        assert.strictEqual(fake.events.length, 1);
        assert.strictEqual(fake.events[0].name, 'aspire/dashboard/operation');
        assert.strictEqual(fake.events[0].isDangerous, true);
    });

    test('telemetry level "off" suppresses regular and error events', () => {
        fake.telemetryLevel = 'off';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.off' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 0);
    });

    test('telemetry level "crash" suppresses regular and error events from our gate', () => {
        // We do not currently expose a crash channel; matching the underlying
        // reporter's behavior, only `'all'` allows usage events and `'error'`
        // or above allows error events. `'crash'` should suppress both.
        fake.telemetryLevel = 'crash';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.crash' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 0);
    });

    test('telemetry level "error" suppresses regular events but allows error events', () => {
        fake.telemetryLevel = 'error';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.errorOnly' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 1);
        assert.strictEqual(fake.events[0].isError, true);
        assert.strictEqual(fake.events[0].name, 'aspire/vscode/debug/runsession/end');
    });

    test('initializeTelemetry constructs the reporter and emits unprefixed event names', () => {
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

            sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.initialized' });

            assert.strictEqual(createdWithKey, 'test-key');
            assert.strictEqual(subscriptions.length, 1);
            assert.strictEqual(fake.events[0].name, 'aspire/vscode/command/invoked');
            assert.strictEqual(fake.events[0].isDangerous, true);
        }
        finally {
            restoreFactory();
        }
    });

    test('withCommandTelemetry emits success outcome', async () => {
        await withCommandTelemetry('cmd.success', () => 42);
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.name, 'aspire/vscode/command/invoked');
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

    test('withCommandTelemetry classifies handled unsuccessful outcomes without rethrowing', async () => {
        const result = await withCommandTelemetry('cmd.handledError', () => ({ success: false, hadOutput: false }));

        assert.deepStrictEqual(result, { success: false, hadOutput: false });
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.properties?.outcome, 'error');
        assert.strictEqual(event.properties?.error_kind, 'HandledError');
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

