import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import * as vscode from 'vscode';
import { __resetCommonPropertiesForTests, __resetTelemetryReporterFactoryForTests, __setReporterForTests, __setTelemetryReporterFactoryForTests, classifyError, initializeTelemetry, isCommandCancellation, sendTelemetryErrorEvent, sendTelemetryEvent, setCommandInvocationListener, setCommonTelemetryProperties, withCommandTelemetry } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
    isError?: boolean;
    isDangerous?: boolean;
}

type TelemetryLevel = 'all' | 'error' | 'crash' | 'off';

function readJsonFile<T>(filePath: string): T {
    return JSON.parse(fs.readFileSync(filePath, 'utf8')) as T;
}

function getExtensionTelemetryPackageVersion(): string {
    const extensionRoot = path.resolve(__dirname, '..', '..');
    const extensionPackage = readJsonFile<{ dependencies?: Record<string, string> }>(path.join(extensionRoot, 'package.json'));
    const telemetryPackage = readJsonFile<{ version: string }>(path.join(extensionRoot, 'node_modules', '@vscode', 'extension-telemetry', 'package.json'));
    assert.strictEqual(extensionPackage.dependencies?.['@vscode/extension-telemetry'], telemetryPackage.version);
    return telemetryPackage.version;
}

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

    sendTelemetryEvent(): void {
        throw new Error('Telemetry must use sendDangerousTelemetryEvent so VS Code does not add the extension-id prefix.');
    }

    sendTelemetryErrorEvent(): void {
        throw new Error('Telemetry must use sendDangerousTelemetryErrorEvent so VS Code does not add the extension-id prefix.');
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

    test('sendTelemetryEvent sanitizes property values before the dangerous send path', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cmd.leak user@example.com /Users/alice/source C:\\Users\\bob\\source --token=secret client_secret=secret connectionstring=secret https://storage.example/?sig=signature Authorization: Bearer abc.def-ghi 4fd8856f-0fc4-4c65-9074-c234c5a0898b',
        });

        assert.strictEqual(
            fake.events[0].properties?.command,
            'cmd.leak <email> /Users/<user>/source C:\\Users\\<user>\\source --token=<redacted> client_secret=<redacted> connectionstring=<redacted> https://storage.example/?sig=<redacted> Authorization: Bearer <redacted> <guid>');
    });

    test('sendTelemetryEvent redacts home usernames that contain spaces', () => {
        // The username is a single path segment that can legitimately contain spaces. Redaction must
        // consume the whole segment up to the next separator instead of stopping at the first space
        // (which previously leaked the rest of the username, e.g. `.../<user> Smith/project`).
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'posix /Users/Alice Smith/project win C:\\Users\\Alice Smith\\project home /home/Alice Smith/project',
        });

        assert.strictEqual(
            fake.events[0].properties?.command,
            'posix /Users/<user>/project win C:\\Users\\<user>\\project home /home/<user>/project');
    });

    test('sendTelemetryEvent redacts exact home directories that contain spaces', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: '/Users/Alice Smith',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'C:\\Users\\Alice Smith',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: '/home/Alice Smith',
        });

        assert.strictEqual(fake.events[0].properties?.command, '/Users/<user>');
        assert.strictEqual(fake.events[1].properties?.command, 'C:\\Users\\<user>');
        assert.strictEqual(fake.events[2].properties?.command, '/home/<user>');
    });

    test('sendTelemetryEvent redacts embedded terminal home directories that contain spaces', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cwd=/Users/Alice Smith --flag',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cwd="C:\\Users\\Alice Smith" --flag',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cwd=/home/Alice Smith',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cwd=/Users/Alice Smith -f',
        });
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: 'cwd=/Users/Alice Bob Carol Dave --flag',
        });

        assert.strictEqual(fake.events[0].properties?.command, 'cwd=/Users/<user> --flag');
        assert.strictEqual(fake.events[1].properties?.command, 'cwd="C:\\Users\\<user>" --flag');
        assert.strictEqual(fake.events[2].properties?.command, 'cwd=/home/<user>');
        assert.strictEqual(fake.events[3].properties?.command, 'cwd=/Users/<user> -f');
        assert.strictEqual(fake.events[4].properties?.command, 'cwd=/Users/<user> --flag');
    });

    test('sendTelemetryEvent redacts the current home directory before shell and punctuation boundaries', () => {
        const originalHome = process.env.HOME;

        try {
            process.env.HOME = '/Users/Alice Smith';

            sendTelemetryEvent('aspire/vscode/command/invoked', {
                command: 'open /Users/Alice Smith | cat',
            });
            sendTelemetryEvent('aspire/vscode/command/invoked', {
                command: 'path is /Users/Alice Smith, ok building /Users/Alice Smith failed',
            });

            assert.strictEqual(fake.events[0].properties?.command, 'open /Users/<user> | cat');
            assert.strictEqual(fake.events[1].properties?.command, 'path is /Users/<user>, ok building /Users/<user> failed');
        }
        finally {
            if (originalHome === undefined) {
                delete process.env.HOME;
            }
            else {
                process.env.HOME = originalHome;
            }
        }
    });

    test('sendTelemetryEvent redacts quoted secrets', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: '--token="secret" token=\'secret\' password=\'secret\' https://storage.example/?sig="signature"&next=1',
        });

        assert.strictEqual(
            fake.events[0].properties?.command,
            '--token="<redacted>" token=\'<redacted>\' password=\'<redacted>\' https://storage.example/?sig="<redacted>"&next=1');
    });

    test('sendTelemetryEvent redacts quoted secrets that contain spaces', () => {
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: '--token="secret value" token=\'secret value\' https://storage.example/?sig="secret value"&next=1',
        });

        assert.strictEqual(
            fake.events[0].properties?.command,
            '--token="<redacted>" token=\'<redacted>\' https://storage.example/?sig="<redacted>"&next=1');
    });

    test('sendTelemetryEvent does not over-redact path segments after a spaced home username', () => {
        // Only the username segment should be redacted. A following, unrelated path segment (which may
        // itself contain spaces) and adjacent tokens must survive because redaction stops at the
        // separator that ends the username.
        sendTelemetryEvent('aspire/vscode/command/invoked', {
            command: '/Users/Alice Smith/some folder/file --flag /Users/alice C:\\Users\\bob\\x',
        });

        assert.strictEqual(
            fake.events[0].properties?.command,
            '/Users/<user>/some folder/file --flag /Users/<user> C:\\Users\\<user>\\x');
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

    test('telemetry level "all" allows both regular and error events', () => {
        fake.telemetryLevel = 'all';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.allRegular' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });

        assert.strictEqual(fake.events.length, 2);
        assert.strictEqual(fake.events[0].isError, undefined);
        assert.strictEqual(fake.events[0].isDangerous, true);
        assert.strictEqual(fake.events[1].isError, true);
        assert.strictEqual(fake.events[1].isDangerous, true);
    });

    test('telemetry level is consulted per emit so mid-session changes are honored immediately', () => {
        fake.telemetryLevel = 'all';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.beforeFlip' });
        fake.telemetryLevel = 'off';
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.afterFlip' });
        assert.strictEqual(fake.events.length, 1);
        assert.strictEqual(fake.events[0].properties?.command, 'cmd.beforeFlip');

        fake.telemetryLevel = 'error';
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 2);
        assert.strictEqual(fake.events[1].isError, true);
    });

    test('uninitialized reporter drops regular and error events silently', () => {
        restore();
        sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.noReporter' });
        sendTelemetryErrorEvent('aspire/vscode/debug/runsession/end', {
            resource_type: 'project',
            mode: 'run',
            exit_code_bucket: 'nonzero',
            end_reason: 'process_exit',
        });
        assert.strictEqual(fake.events.length, 0);

        restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
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
                        aiKey: 'test-key',
                        version: '1.2.3'
                    }
                },
                subscriptions
            } as unknown as vscode.ExtensionContext);

            sendTelemetryEvent('aspire/vscode/command/invoked', { command: 'cmd.initialized' });

            assert.strictEqual(createdWithKey, 'test-key');
            assert.strictEqual(subscriptions.length, 1);
            assert.strictEqual(fake.events[0].name, 'aspire/vscode/command/invoked');
            assert.strictEqual(fake.events[0].isDangerous, true);
            assert.strictEqual(fake.events[0].properties?.['common.extname'], 'microsoft-aspire.aspire-vscode');
            assert.strictEqual(fake.events[0].properties?.['common.extversion'], '1.2.3');
            assert.strictEqual(fake.events[0].properties?.['common.telemetryclientversion'], getExtensionTelemetryPackageVersion());
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
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.properties?.outcome, 'error');
        assert.strictEqual(event.properties?.error_kind, 'HandledError');
    });

    test('withCommandTelemetry records a handled failure error_kind when the result supplies one', async () => {
        const result = await withCommandTelemetry('cmd.handledKind', () => ({ success: false, errorKind: 'ResourceNotFound' }));

        assert.deepStrictEqual(result, { success: false, errorKind: 'ResourceNotFound' });
        assert.strictEqual(fake.events.length, 1);
        const event = fake.events[0];
        assert.strictEqual(event.properties?.outcome, 'error');
        assert.strictEqual(event.properties?.error_kind, 'ResourceNotFound');
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
