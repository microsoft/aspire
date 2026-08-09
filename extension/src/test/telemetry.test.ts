import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import * as vscode from 'vscode';
import {
    __resetCommonPropertiesForTests,
    __resetTelemetryLoggerFactoryForTests,
    __resetTelemetryReporterFactoryForTests,
    __setReporterForTests,
    __setTelemetryLoggerFactoryForTests,
    __setTelemetryReporterFactoryForTests,
    classifyError,
    initializeTelemetry,
    isCommandCancellation,
    isExtensionTelemetryEnabled,
    sendTelemetryErrorEvent,
    sendTelemetryEvent,
    setCommandInvocationListener,
    setCommonTelemetryProperties,
    withCommandTelemetry,
} from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
    isError?: boolean;
    isDangerous?: boolean;
}

type TelemetryLevel = 'all' | 'error' | 'crash' | 'off';

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];
    public telemetryLevel: TelemetryLevel = 'all';

    sendTelemetryEvent(): void {
        throw new Error('Telemetry must pass through the Aspire transport bridge.');
    }

    sendTelemetryErrorEvent(): void {
        throw new Error('Telemetry must pass through the Aspire transport bridge.');
    }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isDangerous: true });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements, isError: true, isDangerous: true });
    }

    sendDangerousTelemetryException(error: Error, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({
            name: error.name,
            properties: { ...properties, message: error.message },
            measurements,
            isError: true,
            isDangerous: true,
        });
    }

    sendRawTelemetryEvent(): void { /* not used */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

function createPrefixingTelemetryLogger(
    sender: vscode.TelemetrySender,
    options: vscode.TelemetryLoggerOptions,
): vscode.TelemetryLogger {
    const changeEmitter = new vscode.EventEmitter<vscode.TelemetryLogger>();
    const send = (eventName: string, data?: Record<string, unknown>) => {
        const telemetryData = data as {
            properties?: Record<string, string>;
            measurements?: Record<string, number>;
        } | undefined;
        sender.sendEventData(`microsoft-aspire.aspire-vscode/${eventName}`, {
            properties: {
                ...telemetryData?.properties,
                ...options.additionalCommonProperties,
                'common.sqmid': 'test-sqm-id',
            },
            measurements: telemetryData?.measurements,
        });
    };

    return {
        isUsageEnabled: true,
        isErrorsEnabled: true,
        logUsage: send,
        logError(eventNameOrException, data) {
            if (typeof eventNameOrException === 'string') {
                send(eventNameOrException, data);
            }
            else {
                sender.sendErrorData(eventNameOrException, data ?? {
                    ...options.additionalCommonProperties,
                    'common.sqmid': 'test-sqm-id',
                });
            }
        },
        onDidChangeEnableStates: changeEmitter.event,
        dispose() {
            changeEmitter.dispose();
        },
    };
}

function createContext(subscriptions: vscode.Disposable[]): vscode.ExtensionContext {
    return {
        extension: {
            id: 'microsoft-aspire.aspire-vscode',
            packageJSON: {
                aiKey: 'test-key',
                version: '1.2.3',
            },
        },
        subscriptions,
    } as unknown as vscode.ExtensionContext;
}

suite('telemetry utilities', () => {
    let fake: FakeTelemetryReporter;
    let restore: () => void;

    setup(() => {
        fake = new FakeTelemetryReporter();
        restore = __setReporterForTests(fake as unknown as TelemetryReporter);
        __resetCommonPropertiesForTests();
    });

    teardown(() => {
        setCommandInvocationListener(undefined);
        restore();
        __resetTelemetryReporterFactoryForTests();
        __resetTelemetryLoggerFactoryForTests();
        __resetCommonPropertiesForTests();
    });

    test('sendTelemetryEvent merges common properties', () => {
        setCommonTelemetryProperties({ apphost_languages: 'csharp', apphost_present: 'true' });
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.x' });

        assert.strictEqual(fake.events.length, 1);
        assert.deepStrictEqual(fake.events[0].properties, {
            apphost_languages: 'csharp',
            apphost_present: 'true',
            command: 'cmd.x',
        });
    });

    test('setCommonTelemetryProperties replaces and clears keys', () => {
        setCommonTelemetryProperties({ apphost_languages: 'first', apphost_present: 'keep' });
        setCommonTelemetryProperties({ apphost_languages: undefined });
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.y' });

        assert.deepStrictEqual(fake.events[0].properties, {
            apphost_present: 'keep',
            command: 'cmd.y',
        });
    });

    test('usage and dashboard events keep their registry wire names', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.prefixed' });
        sendTelemetryEvent('aspire/dashboard/operation', {
            dashboard_event_name: 'aspire/dashboard/command',
            result: 'success',
        });

        assert.deepStrictEqual(fake.events.map(event => event.name), [
            'aspire/vscode/command/invoked',
            'aspire/dashboard/operation',
        ]);
        assert.ok(fake.events.every(event => event.isDangerous === true));
    });

    test('sendTelemetryErrorEvent uses the error-level logger path', () => {
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        }, { duration_ms: 12 });

        assert.strictEqual(fake.events.length, 1);
        assert.strictEqual(fake.events[0].name, 'aspire/vscode/debug/runsession/end');
        assert.strictEqual(fake.events[0].isError, true);
        assert.strictEqual(fake.events[0].measurements?.duration_ms, 12);
    });

    test('telemetry levels are consulted on every emit', () => {
        fake.telemetryLevel = 'off';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.off' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 0);

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

        fake.telemetryLevel = 'all';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.all' });
        assert.strictEqual(fake.events.length, 2);

        fake.telemetryLevel = 'crash';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.crash' });
        assert.strictEqual(fake.events.length, 2);
    });

    test('isExtensionTelemetryEnabled includes errors-only telemetry', () => {
        fake.telemetryLevel = 'error';
        assert.strictEqual(isExtensionTelemetryEnabled(), true);

        fake.telemetryLevel = 'crash';
        assert.strictEqual(isExtensionTelemetryEnabled(), false);
    });

    test('uninitialized telemetry drops regular and error events', () => {
        restore();
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.noReporter' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });

        assert.strictEqual(fake.events.length, 0);
    });

    test('initializeTelemetry preserves VS Code guarantees and strips only the wire prefix', async () => {
        restore();
        let createdWithKey: string | undefined;
        let reporterOptions: vscode.TelemetryLoggerOptions | undefined;
        let loggerOptions: vscode.TelemetryLoggerOptions | undefined;
        let initializedLogger: vscode.TelemetryLogger | undefined;
        const restoreReporterFactory = __setTelemetryReporterFactoryForTests((aiKey, options) => {
            createdWithKey = aiKey;
            reporterOptions = options;
            return fake as unknown as TelemetryReporter;
        });
        const restoreLoggerFactory = __setTelemetryLoggerFactoryForTests((sender, options) => {
            loggerOptions = options;
            initializedLogger = createPrefixingTelemetryLogger(sender, options);
            return initializedLogger;
        });
        const subscriptions: vscode.Disposable[] = [];

        try {
            initializeTelemetry(createContext(subscriptions));
            sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.initialized' });
            sendTelemetryEvent('aspire/vscode/engagement/active', undefined, { workspace_folders: 2 });

            assert.strictEqual(createdWithKey, 'test-key');
            assert.strictEqual(subscriptions.length, 1);
            assert.strictEqual(reporterOptions?.ignoreUnhandledErrors, true);
            assert.strictEqual(loggerOptions?.ignoreUnhandledErrors, false);
            assert.strictEqual(fake.events.length, 2);
            assert.strictEqual(fake.events[0].name, 'aspire/vscode/command/invoked');
            assert.strictEqual(fake.events[0].properties?.command, 'cmd.initialized');
            assert.strictEqual(fake.events[0].properties?.['common.sqmid'], 'test-sqm-id');
            assert.strictEqual(fake.events[0].properties?.['common.os'], process.platform);
            assert.strictEqual(fake.events[0].properties?.['common.nodeArch'], process.arch);
            assert.ok(fake.events[0].properties?.['common.telemetryclientversion']);
            assert.strictEqual(fake.events[1].properties?.['common.sqmid'], 'test-sqm-id');
            assert.strictEqual(fake.events[1].measurements?.workspace_folders, 2);

            initializedLogger?.logError(new Error('bridge failure'));
            assert.strictEqual(fake.events.length, 3);
            assert.strictEqual(fake.events[2].name, 'Error');
            assert.strictEqual(fake.events[2].isError, true);
            assert.strictEqual(fake.events[2].properties?.['common.sqmid'], 'test-sqm-id');
            assert.strictEqual(fake.events[2].properties?.['common.os'], process.platform);
        }
        finally {
            await Promise.resolve(subscriptions[0]?.dispose());
            restoreLoggerFactory();
            restoreReporterFactory();
        }
    });

    test('real VS Code logger keeps telemetry local in extension test hosts', async () => {
        restore();
        const restoreReporterFactory = __setTelemetryReporterFactoryForTests(() => fake as unknown as TelemetryReporter);
        const subscriptions: vscode.Disposable[] = [];

        try {
            initializeTelemetry(createContext(subscriptions));
            assert.strictEqual(isExtensionTelemetryEnabled(), true);

            sendTelemetryEvent('aspire/vscode/command/invoked', {
                command: 'cmd.loggingOnly user@example.com /Users/alice/project --token =   secret',
            });
            sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
                resource_type: 'project',
                mode: 'run',
                exit_code_bucket: 'nonzero',
                end_reason: 'process_exit',
            });

            assert.strictEqual(fake.events.length, 0, 'logging-only mode must not invoke the transport sender');
        }
        finally {
            await Promise.resolve(subscriptions[0]?.dispose());
            restoreReporterFactory();
        }
    });

    test('withCommandTelemetry emits success outcome', async () => {
        await withCommandTelemetry('cmd.success', () => 42);

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
        await assert.rejects(withCommandTelemetry('cmd.error', () => { throw new TypeError('bad'); }));

        assert.strictEqual(fake.events[0].properties?.outcome, 'error');
        assert.strictEqual(fake.events[0].properties?.error_kind, 'TypeError');
    });

    test('withCommandTelemetry drops non-identifier error names', async () => {
        const err = new Error('sensitive@example.com /Users/alice/project');
        err.name = 'Bad Error /Users/alice/project';

        await assert.rejects(withCommandTelemetry('cmd.invalidErrorName', () => { throw err; }));

        assert.strictEqual(fake.events[0].properties?.outcome, 'error');
        assert.strictEqual(fake.events[0].properties?.error_kind, 'Error');
        assert.strictEqual(classifyError(err), 'Error');
    });

    test('withCommandTelemetry classifies handled unsuccessful outcomes without rethrowing', async () => {
        const result = await withCommandTelemetry('cmd.handledError', () => ({ success: false, hadOutput: false }));

        assert.deepStrictEqual(result, { success: false, hadOutput: false });
        assert.strictEqual(fake.events[0].properties?.outcome, 'error');
        assert.strictEqual(fake.events[0].properties?.error_kind, 'HandledError');
    });

    test('withCommandTelemetry records and normalizes handled error kinds', async () => {
        await withCommandTelemetry('cmd.handledKind', () => ({ success: false, errorKind: 'ResourceNotFound' }));
        await withCommandTelemetry('cmd.invalidHandledErrorKind', () => ({
            success: false,
            errorKind: 'Bad Error C:\\Users\\bob',
        }));

        assert.strictEqual(fake.events[0].properties?.error_kind, 'ResourceNotFound');
        assert.strictEqual(fake.events[1].properties?.error_kind, 'Error');
    });

    test('withCommandTelemetry classifies cancellations without an error kind', async () => {
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
        const canceled = new Error('Canceled');
        canceled.name = 'Canceled';
        const cancellationError = new Error('CancellationError thrown');
        cancellationError.name = 'CancellationError';

        assert.strictEqual(isCommandCancellation(canceled), true);
        assert.strictEqual(isCommandCancellation(cancellationError), true);
        assert.strictEqual(isCommandCancellation(new Error('canceled')), true);
        assert.strictEqual(isCommandCancellation('Canceled'), true);
        assert.strictEqual(isCommandCancellation(new Error('something else')), false);
        assert.strictEqual(isCommandCancellation(undefined), false);
    });
});
