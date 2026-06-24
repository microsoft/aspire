import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession, buildAspireCommandArgs, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import { AspireResourceExtendedDebugConfiguration } from '../dcp/types';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }
    sendDangerousTelemetryEvent(): void { /* not used here */ }
    sendDangerousTelemetryErrorEvent(): void { /* not used here */ }
    sendRawTelemetryEvent(): void { /* not used here */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('AspireDebugSession tests', () => {
    const tempDirs: string[] = [];

    function makeTempDir(): string {
        const parent = join(process.cwd(), '.test-tmp');
        mkdirSync(parent, { recursive: true });
        const dir = mkdtempSync(join(parent, 'aspire-debug-session-'));
        tempDirs.push(dir);
        return dir;
    }

    teardown(() => {
        sinon.restore();
        __resetCommonPropertiesForTests();
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                rmSync(dir, { recursive: true, force: true });
            }
        }
        tempDirs.length = 0;
    });

    test('suppresses the Aspire CLI first-run banner for extension-managed launches', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

        await waitFor(() => spawnStub.calledOnce);
        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[0], [
            'run',
            '--start-debug-session',
            '--nologo',
            '--apphost',
            '/workspace/apphost.cs',
        ]);
    });

    test('reports unknown AppHost target version in start telemetry before async enrichment', async () => {
        const tempDir = makeTempDir();
        const appHostPath = join(tempDir, 'apphost.cs');
        writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@13.6.0

var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);
`);
        const fake = new FakeTelemetryReporter();
        const restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

            await waitFor(() => fake.events.some(event => event.name === 'debug/apphost/start'));
            const event = fake.events.find(event => event.name === 'debug/apphost/start');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, 'unknown');
        }
        finally {
            restoreReporter();
        }
    });

    test('emits AppHost start telemetry before target version resolution completes', async () => {
        const tempDir = makeTempDir();
        const appHostPath = join(tempDir, 'apphost.cs');
        writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@13.6.0

var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);
`);
        const fake = new FakeTelemetryReporter();
        const restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        let resolveTargetVersion: ((value: string) => void) | undefined;
        const targetVersionPromise = new Promise<string>(resolve => {
            resolveTargetVersion = resolve;
        });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').returns(targetVersionPromise);

        let eventsAtSpawn: RecordedEvent[] = [];
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').callsFake(async () => {
            eventsAtSpawn = [...fake.events];
        });

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

            await waitFor(() => spawnStub.calledOnce);
            const event = eventsAtSpawn.find(event => event.name === 'debug/apphost/start');
            assert.ok(event, 'Expected debug/apphost/start to be emitted before spawnAspireCommand.');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, 'unknown');
        }
        finally {
            resolveTargetVersion?.('13.6.0');
            restoreReporter();
        }
    });

    test('emits AppHost end telemetry when disposed before launch filesystem check completes', async () => {
        const fake = new FakeTelemetryReporter();
        const restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const dcpServer = {
            takeDebugSessionAggregateStats: sinon.stub().returns({
                anyNonZeroExit: false,
                distinctResourceTypes: [],
                totalChildSessions: 0,
            }),
        };
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession as any, 'isDirectory').returns(new Promise<boolean>(() => { }));

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            aspireDebugSession.dispose();

            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'debug/apphost/end'));

            const event = fake.events.find(event => event.name === 'debug/apphost/end');
            assert.ok(event, 'Expected debug/apphost/end when disposal races with launch startup.');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, 'unknown');
        }
        finally {
            restoreReporter();
        }
    });

    test('reports AppHost target version in end telemetry', async () => {
        const tempDir = makeTempDir();
        const appHostPath = join(tempDir, 'apphost.cs');
        writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@13.6.0

var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);
`);
        const fake = new FakeTelemetryReporter();
        const restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const dcpServer = {
            takeDebugSessionAggregateStats: sinon.stub().returns({
                anyNonZeroExit: false,
                distinctResourceTypes: ['project'],
                totalChildSessions: 1,
            }),
        };
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await waitFor(() => fake.events.some(event => event.name === 'debug/apphost/start'));
            const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            aspireDebugSession.dispose();
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'debug/apphost/end'));

            const event = fake.events.find(event => event.name === 'debug/apphost/end');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, '13.6.0');
        }
        finally {
            restoreReporter();
        }
    });

    test('redacts debug configuration environment fields from logs by default', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, false);

        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.strictEqual(loggableConfig.environmentVariables, '<redacted>');
    });

    test('redacts MAUI environmentVariables even when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.deepStrictEqual(loggableConfig.env, { SECRET_TOKEN: 'env-secret' });
        assert.strictEqual(loggableConfig.environmentVariables, '<redacted>');
    });

    test('responds to breakpoint requests with a DAP breakpoint body', () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const messages: any[] = [];
        const subscription = aspireDebugSession.onDidSendMessage(message => messages.push(message));

        try {
            aspireDebugSession.handleMessage({
                command: 'setBreakpoints',
                seq: 4,
                arguments: {
                    breakpoints: [
                        { line: 27, column: 5 },
                    ],
                },
            });

            assert.deepStrictEqual(messages, [
                {
                    type: 'response',
                    seq: 1,
                    request_seq: 4,
                    success: true,
                    command: 'setBreakpoints',
                    body: {
                        breakpoints: [
                            {
                                id: 1,
                                verified: false,
                                line: 27,
                                column: 5,
                            },
                        ],
                    },
                },
            ]);
        }
        finally {
            subscription.dispose();
        }
    });

    test('starts resource debug sessions from the workspace folder containing the project', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        } as vscode.WorkspaceFolder;
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'API',
            request: 'launch',
            program: '/workspace/Api/Api.dll',
            cwd: '/workspace/Api',
        } as AspireResourceExtendedDebugConfiguration;
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(workspaceFolder);
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(false);

        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.strictEqual(getWorkspaceFolderStub.calledOnceWith(vscode.Uri.file('/workspace/Api')), true);
        assert.strictEqual(startDebuggingStub.calledOnce, true);
        assert.strictEqual(startDebuggingStub.firstCall.args[0], workspaceFolder);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], parentDebugSession);
    });

    test('retries MAUI resource debug sessions when the first start attempt is canceled', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging');
        startDebuggingStub.onFirstCall().resolves(false);
        startDebuggingStub.onSecondCall().callsFake(async (_folder, configuration) => {
            startSessionCallback?.({
                id: 'maui-session',
                type: 'maui',
                name: 'MAUI',
                configuration: configuration as vscode.DebugConfiguration,
            } as vscode.DebugSession);
            return true;
        });
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await clock.tickAsync(5000);
        const session = await sessionPromise;

        assert.strictEqual(session?.id, 'maui-session');
        assert.strictEqual(startDebuggingStub.callCount, 2);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], undefined);
        assert.strictEqual(startDebuggingStub.secondCall.args[2], undefined);
    });

    test('does not retry MAUI resource debug sessions while the first start is still pending', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let resolveStart: ((value: boolean) => void) | undefined;
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStart = resolve;
        });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await clock.tickAsync(95_001);
        const startAttemptsWhilePending = startDebuggingStub.callCount;
        startSessionCallback?.({
            id: 'maui-session',
            type: 'maui',
            name: 'MAUI',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession);
        resolveStart!(true);
        const session = await sessionPromise;

        assert.strictEqual(session?.id, 'maui-session');
        assert.strictEqual(startAttemptsWhilePending, 1);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], undefined);
    });

    test('stops MAUI resource debug sessions that start after Aspire session disposal', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let resolveStart: ((value: boolean) => void) | undefined;
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStart = resolve;
        });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        const lateMauiSession = {
            id: 'maui-session',
            type: 'maui',
            name: 'MAUI',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        aspireDebugSession.dispose();
        startSessionCallback?.(lateMauiSession);
        resolveStart!(true);
        const session = await sessionPromise;

        assert.strictEqual(session, undefined);
        assert.strictEqual(stopDebuggingStub.calledWith(lateMauiSession), true);
    });

    suite('buildAspireCommandArgs', () => {
        test('appends extension arguments when command has no app argument separator', () => {
            const args = buildAspireCommandArgs('run', ['--isolated'], ['--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);

            assert.deepStrictEqual(args, ['run', '--isolated', '--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);
        });

        test('inserts extension arguments before app argument separator', () => {
            const args = buildAspireCommandArgs('run', ['--isolated', '--', '--custom-arg', 'value'], ['--apphost', '/workspace/AppHost.csproj']);

            assert.deepStrictEqual(args, ['run', '--isolated', '--apphost', '/workspace/AppHost.csproj', '--', '--custom-arg', 'value']);
        });
    });

    async function waitFor(predicate: () => boolean): Promise<void> {
        const start = Date.now();
        while (!predicate()) {
            if (Date.now() - start > 5000) {
                throw new Error('Timed out waiting for condition.');
            }

            await new Promise(resolve => setTimeout(resolve, 10));
        }
    }

    async function waitForWithFakeClock(clock: sinon.SinonFakeTimers, predicate: () => boolean): Promise<void> {
        const timeoutAt = clock.now + 5000;
        while (!predicate()) {
            if (clock.now > timeoutAt) {
                throw new Error('Timed out waiting for condition.');
            }

            await clock.tickAsync(10);
        }
    }
});
