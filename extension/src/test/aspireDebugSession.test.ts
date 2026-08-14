import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { dirname, join } from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../utils/process/cliProcess';
import * as debuggerExtensionsModule from '../debugger/debuggerExtensions';
import { AspireDebugSession, buildAspireCommandArgs, getLoggableDebugConfiguration, markDebugConfigurationEnvironmentSensitive } from '../debugger/AspireDebugSession';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { AspireResourceExtendedDebugConfiguration, RustLaunchConfiguration } from '../dcp/types';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';
import { aspireDashboard, debugSessionStopTimedOut } from '../loc/strings';
import { registerRunCleanup } from '../debugger/runCleanupRegistry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        // Extension code now bypasses this path; recording here would only
        // see a regression to the prefixed channel. Kept as a typed no-op
        // so the fake still satisfies the TelemetryReporter shape.
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
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

    test('extension shutdown reuses an in-flight CLI stop request', async () => {
        let completeStop!: () => void;
        const stopRequest = new Promise<void>(resolve => {
            completeStop = resolve;
        });
        const stopCli = sinon.stub().returns(stopRequest);
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._rpcClient = { stopCli };

        const firstRequest = aspireDebugSession.requestCliStopForExtensionShutdown();
        const secondRequest = aspireDebugSession.requestCliStopForExtensionShutdown();

        assert.strictEqual(secondRequest, firstRequest);
        sinon.assert.calledOnce(stopCli);

        completeStop();
        await firstRequest;
    });

    test('extension shutdown retries a failed CLI stop request', async () => {
        const expectedError = new Error('CLI stop failed');
        const stopCli = sinon.stub();
        stopCli.onFirstCall().rejects(expectedError);
        stopCli.onSecondCall().resolves();
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._rpcClient = { stopCli };

        await assert.rejects(aspireDebugSession.requestCliStopForExtensionShutdown(), error => error === expectedError);
        await aspireDebugSession.requestCliStopForExtensionShutdown();

        sinon.assert.calledTwice(stopCli);
    });

    test('extension shutdown waits for a CLI RPC client that connects after the stop request', async () => {
        let onNewConnection!: (client: { debugSessionId: string; stopCli: () => Promise<void> }) => void;
        const stopCli = sinon.stub().resolves();
        const cliProcess = createFakeCliProcess(4320);
        sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const aspireDebugSession = createSessionForSpawn(
            async () => '/usr/local/bin/aspire',
            () => { },
            callback => {
                onNewConnection = callback;
                return { dispose: sinon.stub() };
            });

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        const stopRequest = aspireDebugSession.requestCliStopForExtensionShutdown();
        let stopSettled = false;
        void stopRequest.then(() => { stopSettled = true; });
        await Promise.resolve();

        assert.strictEqual(stopSettled, false);

        onNewConnection({ debugSessionId: aspireDebugSession.debugSessionId, stopCli });
        await stopRequest;

        sinon.assert.calledOnce(stopCli);
    });

    test('spawns the Aspire CLI as a process-group leader and retains the child process', async () => {
        const cliProcess = createFakeCliProcess(4321);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        try {
            const aspireDebugSession = createSessionForSpawn();

            await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

            const options = spawnStub.firstCall.args[3];
            // Without a process group there is no way to signal the AppHost and resource processes
            // the CLI owns, and without retaining the child there is nothing to signal at all.
            assert.strictEqual(options?.createProcessGroup, true);
            assert.strictEqual((aspireDebugSession as any)._cliProcess, cliProcess);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('terminateCliProcessTree signals a running CLI process and still collects an exited one', () => {
        // `terminateCliProcess` is stubbed rather than executed: on Windows it shells out to
        // `taskkill /pid <pid> /t` instead of calling `child.kill`, so running it for real would
        // both fail this assertion on the Windows CI agents and signal whatever process happens to
        // own the made-up PID there.
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        const running = createFakeCliProcess(4322);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession as any)._cliProcess = running;

        aspireDebugSession.terminateCliProcessTree();

        // The cooperative `stopCli` RPC cannot terminate the process, so the signal is what
        // actually ends the CLI and the resource tree beneath it.
        sinon.assert.calledOnce(terminateStub);
        assert.strictEqual(terminateStub.firstCall.args[0], running);

        const exited = createFakeCliProcess(4323, 0);
        const exitedAspireDebugSession = createSessionForSpawn();
        (exitedAspireDebugSession as any)._cliProcess = exited;

        exitedAspireDebugSession.terminateCliProcessTree();

        // An exited leader is still forwarded: `terminateCliProcess` reaps the surviving members of
        // its managed process group, which is the only path that collects an AppHost and resource
        // processes that outlived the CLI.
        sinon.assert.calledTwice(terminateStub);
        assert.strictEqual(terminateStub.secondCall.args[0], exited);
    });

    test('terminateCliProcessTree is idempotent after signalling a CLI process', () => {
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        const cliProcess = createFakeCliProcess(4324);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession as any)._cliProcess = cliProcess;

        aspireDebugSession.terminateCliProcessTree({ force: true });
        aspireDebugSession.terminateCliProcessTree();

        sinon.assert.calledOnce(terminateStub);
        assert.strictEqual(terminateStub.firstCall.args[0], cliProcess);
        assert.deepStrictEqual(terminateStub.firstCall.args[2], { force: true });
    });

    test('a POSIX CLI process that exits on its own still has its process group collected', async () => {
        // The behaviour under test is selected by `process.platform`, so it has to be pinned rather
        // than inherited from whichever agent runs the suite. Without this the test asserts POSIX
        // behaviour on the Windows agents, where the branch it covers deliberately does not run.
        const platformStub = sinon.stub(process, 'platform').value('linux');
        // Already exited: the leader is gone by the time the exit callback runs, which is exactly
        // the state the old early return skipped on.
        const cliProcess = createFakeCliProcess(4325, 0);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();

        try {
            await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

            spawnStub.firstCall.args[3]?.exitCallback?.(0);

            // The CLI is gone but the AppHost and resource processes in its detached group need not
            // be, and once the leader's PID is released the group id can be recycled — so the
            // collection has to happen here rather than on a later timer.
            sinon.assert.calledOnceWithExactly(
                terminateStub,
                cliProcess,
                `Aspire CLI for debug session ${aspireDebugSession.debugSessionId}`,
                { force: true });
        }
        finally {
            platformStub.restore();
        }
    });

    test('a Windows CLI process that exits on its own is not swept by stale PID', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const cliProcess = createFakeCliProcess(4327, 0);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();

        try {
            await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

            spawnStub.firstCall.args[3]?.exitCallback?.(0);

            // On Windows, taskkill can only walk the tree while the target PID still names a live
            // process. The close callback runs after that PID can be recycled, so a force sweep here
            // is both unreliable for descendants and unsafe for an unrelated process that reused it.
            sinon.assert.notCalled(terminateStub);
        }
        finally {
            platformStub.restore();
        }
    });

    test('a Windows CLI process that exits on its own is not swept after the cooperative grace period', async () => {
        // The exit callback runs `dispose()`, which re-runs the CLI disposable and schedules the
        // forced escalation. Asserting only at exit time therefore proves nothing about the sweep
        // that actually reaches taskkill, so this test has to advance past the grace period.
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const cliProcess = createFakeCliProcess(4328, 0);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();

        try {
            await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

            spawnStub.firstCall.args[3]?.exitCallback?.(0);

            // Well past the 10s cooperative grace period, so any scheduled escalation has fired.
            await clock.tickAsync(30_000);

            // The recorded PID named a process that has already exited, so Windows may have handed
            // it to something unrelated by now. Nothing may aim taskkill at it.
            sinon.assert.notCalled(terminateStub);
        }
        finally {
            platformStub.restore();
            clock.restore();
        }
    });

    test('a disposed Windows CLI process that exits releases extension ownership', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const cliProcess = createFakeCliProcess(4329, 0);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const removeAspireDebugSession = sinon.stub();
        sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn(
            async () => '/usr/local/bin/aspire',
            removeAspireDebugSession);

        try {
            await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

            aspireDebugSession.dispose();
            spawnStub.firstCall.args[3]?.exitCallback?.(0);

            // dispose() keeps a stopped session registered while the delayed CLI termination is
            // pending. When the Windows close callback retires that PID without signalling it, the
            // ordered shutdown releases ownership after its in-flight parent stop has settled.
            await aspireDebugSession.stopDebugging();
            sinon.assert.calledOnceWithExactly(removeAspireDebugSession, aspireDebugSession);
        }
        finally {
            platformStub.restore();
        }
    });

    test('a forced CLI process tree termination is not repeated by the exit callback', async () => {
        const cliProcess = createFakeCliProcess(4326, 0);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        aspireDebugSession.terminateCliProcessTree({ force: true });
        spawnStub.firstCall.args[3]?.exitCallback?.(0);

        sinon.assert.calledOnceWithExactly(
            terminateStub,
            cliProcess,
            `Aspire CLI for debug session ${aspireDebugSession.debugSessionId}`,
            { force: true });
    });

    test('a launch that resolves the CLI path after disposal does not spawn an orphan CLI', async () => {
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        let releaseCliPath!: (cliPath: string) => void;
        const cliPath = new Promise<string>(resolve => {
            releaseCliPath = resolve;
        });
        let cliPathRequested!: () => void;
        const cliPathRequestObserved = new Promise<void>(resolve => {
            cliPathRequested = resolve;
        });
        const aspireDebugSession = createSessionForSpawn(() => {
            cliPathRequested();
            return cliPath;
        });

        const spawning = aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');
        await cliPathRequestObserved;
        // Deactivation can complete while the CLI path is still resolving. Set the state `dispose()`
        // establishes rather than calling it, so this covers the spawn guard alone and not VS Code's
        // parent-session teardown.
        (aspireDebugSession as any)._disposed = true;
        releaseCliPath('/usr/local/bin/aspire');
        await spawning;

        // A detached process group spawned here would outlive the extension host itself.
        sinon.assert.notCalled(spawnStub);
        assert.strictEqual((aspireDebugSession as any)._cliProcess, undefined);
        sinon.assert.calledWithMatch(infoStub, 'Skipping Aspire CLI launch for disposed or shutting-down debug session');
    });

    test('a launch that resolves the CLI path after extension shutdown was requested does not spawn an orphan CLI', async () => {
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        let releaseCliPath!: (cliPath: string) => void;
        const cliPath = new Promise<string>(resolve => {
            releaseCliPath = resolve;
        });
        let cliPathRequested!: () => void;
        const cliPathRequestObserved = new Promise<void>(resolve => {
            cliPathRequested = resolve;
        });
        const aspireDebugSession = createSessionForSpawn(() => {
            cliPathRequested();
            return cliPath;
        });

        const spawning = aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');
        await cliPathRequestObserved;
        const stopRequest = aspireDebugSession.requestCliStopForExtensionShutdown();
        releaseCliPath('/usr/local/bin/aspire');
        await spawning;
        await stopRequest;

        // The extension context can request shutdown before it has disposed this session. The
        // session must still remember that no later async continuation is allowed to create a
        // detached CLI process after the deactivation force sweep has already run.
        sinon.assert.notCalled(spawnStub);
        assert.strictEqual((aspireDebugSession as any)._cliProcess, undefined);
        sinon.assert.calledWithMatch(infoStub, 'Skipping Aspire CLI launch for disposed or shutting-down debug session');
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

    test('forwards explicit launch configuration provenance to the Aspire CLI', async () => {
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
                __aspireAppHostSelectionOrigin: 'explicit-launch-configuration',
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
        assert.deepStrictEqual(spawnStub.firstCall.args[4], [{
            name: 'ASPIRE_CLI_APPHOST_SELECTION_ORIGIN',
            value: 'explicit-launch-configuration',
        }]);
    });

    test('describes a no-debug launch as an Aspire run session', async () => {
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
        const messages: any[] = [];
        const subscription = aspireDebugSession.onDidSendMessage(message => messages.push(message));

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: true } });

            await waitFor(() => spawnStub.calledOnce);
            const launchOutput = messages.find(message => message.event === 'output')?.body.output;
            assert.strictEqual(launchOutput, '📂  Launching Aspire run session for AppHost /workspace/apphost.cs...\n');
        }
        finally {
            subscription.dispose();
        }
    });

    test('continues to describe a debug launch as an Aspire debug session', async () => {
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
        const messages: any[] = [];
        const subscription = aspireDebugSession.onDidSendMessage(message => messages.push(message));

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

            await waitFor(() => spawnStub.calledOnce);
            const launchOutput = messages.find(message => message.event === 'output')?.body.output;
            assert.strictEqual(launchOutput, '📂  Launching Aspire debug session for AppHost /workspace/apphost.cs...\n');
        }
        finally {
            subscription.dispose();
        }
    });

    test('describes a no-debug directory launch as an Aspire run session', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession as any, 'isDirectory').resolves(true);
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();
        const messages: any[] = [];
        const subscription = aspireDebugSession.onDidSendMessage(message => messages.push(message));

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: true } });

            await waitFor(() => spawnStub.calledOnce);
            const launchOutput = messages.find(message => message.event === 'output')?.body.output;
            assert.strictEqual(launchOutput, '📁  Launching Aspire run session using directory /workspace: attempting to determine effective AppHost...\n');
        }
        finally {
            subscription.dispose();
        }
    });

    test('omits AppHost target version in start telemetry before async enrichment', async () => {
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        let resolveTargetVersion: ((value: string) => void) | undefined;
        const targetVersionPromise = new Promise<string>(resolve => {
            resolveTargetVersion = resolve;
        });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').returns(targetVersionPromise);
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

            await waitFor(() => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/start'));
            const event = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/start');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(Object.prototype.hasOwnProperty.call(event.properties ?? {}, 'apphost_target_version'), false);
            await waitFor(() => spawnStub.calledOnce);
        }
        finally {
            resolveTargetVersion?.('13.6.0');
            restoreReporter();
        }
    });

    test('Rust direct-file telemetry reports the AppHost language', async () => {
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
                program: '/workspace/apphost.rs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').resolves('unknown');
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

            await waitFor(() => spawnStub.calledOnce);
            const event = fake.events.find(candidate => candidate.name === 'aspire/vscode/debug/apphost/start');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'rust');
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
            const event = eventsAtSpawn.find(event => event.name === 'aspire/vscode/debug/apphost/start');
            assert.ok(event, 'Expected debug/apphost/start to be emitted before spawnAspireCommand.');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(Object.prototype.hasOwnProperty.call(event.properties ?? {}, 'apphost_target_version'), false);
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
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').resolves('unknown');
        sinon.stub(aspireDebugSession as any, 'isDirectory').returns(new Promise<boolean>(() => { }));

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            aspireDebugSession.dispose();

            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const event = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(event, 'Expected debug/apphost/end when disposal races with launch startup.');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, 'unknown');
        }
        finally {
            restoreReporter();
        }
    });

    test('does not spawn Aspire when disposed before launch filesystem check resolves', async () => {
        let resolveIsDirectory: ((value: boolean) => void) | undefined;
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession as any, 'isDirectory').returns(new Promise<boolean>(resolve => {
            resolveIsDirectory = resolve;
        }));
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
        sinon.useFakeTimers({ shouldClearNativeTimers: true });
        aspireDebugSession.dispose();
        resolveIsDirectory!(false);
        await Promise.resolve();
        await Promise.resolve();

        assert.strictEqual(spawnStub.called, false);
    });

    test('does not spawn Aspire when disposal completes during CLI path resolution', async () => {
        let resolveCliPath: ((value: string) => void) | undefined;
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
        const connectionSubscription = { dispose: sinon.spy() };
        const rpcServer = {
            onNewConnection: sinon.stub().returns(connectionSubscription),
        };
        const terminalProvider = {
            getAspireCliExecutablePath: sinon.stub().returns(new Promise<string>(resolve => {
                resolveCliPath = resolve;
            })),
        };
        const removeSession = sinon.spy();
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const spawnCliProcessStub = sinon.stub(cliModule, 'spawnCliProcess');
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            rpcServer as any,
            {} as any,
            terminalProvider as any,
            removeSession);

        const spawnPromise = aspireDebugSession.spawnAspireCommand(['run'], '/workspace', true);

        assert.strictEqual(rpcServer.onNewConnection.callCount, 1);
        assert.strictEqual(terminalProvider.getAspireCliExecutablePath.callCount, 1);
        aspireDebugSession.dispose();
        await aspireDebugSession.stopDebugging();
        assert.strictEqual(removeSession.callCount, 1, 'Disposal must finish before CLI path resolution resumes');

        resolveCliPath!('/workspace/aspire');
        await spawnPromise;

        assert.strictEqual(spawnCliProcessStub.callCount, 0);
        assert.strictEqual(connectionSubscription.dispose.callCount, 1);
    });

    test('dispose stops a tracked dashboard debug session when closeDashboardOnDebugEnd is enabled', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: 'Aspire Dashboard',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession = dashboardDebugSession;

        aspireDebugSession.dispose();
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebugging.callCount, 2);
        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);
    });

    test('dispose leaves a tracked dashboard debug session running when closeDashboardOnDebugEnd is disabled', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: 'Aspire Dashboard',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? false as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession = dashboardDebugSession;

        aspireDebugSession.dispose();
        await aspireDebugSession.stopDebugging();

        sinon.assert.calledOnceWithExactly(stopDebugging, parentDebugSession);
        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession, null);
    });

    test('stopDebugging retries a failed dashboard debug session stop', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: 'Aspire Dashboard',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        let dashboardStopAttempts = 0;
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === dashboardDebugSession && dashboardStopAttempts++ === 0) {
                throw new Error('Dashboard stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession = dashboardDebugSession;

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /Dashboard stop failed/);
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebugging.callCount, 3);
        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);
        assert.strictEqual(stopDebugging.thirdCall.args[0], dashboardDebugSession);
    });

    test('stopDebugging ignores a dashboard debug session that already terminated', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        startSessionCallback?.(dashboardDebugSession);
        await openPromise;
        terminateSessionCallback?.(dashboardDebugSession);
        await aspireDebugSession.stopDebugging();

        sinon.assert.calledOnceWithExactly(stopDebugging, parentDebugSession);
    });

    test('stopDebugging retries a failed dashboard stop that started during shutdown', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        }));
        let dashboardStopAttempts = 0;
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === dashboardDebugSession && dashboardStopAttempts++ === 0) {
                throw new Error('Late dashboard stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        const firstStop = aspireDebugSession.stopDebugging();
        startSessionCallback?.(dashboardDebugSession);
        resolveStartDebugging?.(true);
        await openPromise;

        await firstStop;

        assert.strictEqual(stopDebugging.callCount, 3);
        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.thirdCall.args[0], parentDebugSession);
    });

    test('stopDebugging closes a dashboard session whose start promise remains pending', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        }));
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        const stopPromise = aspireDebugSession.stopDebugging();
        startSessionCallback?.(dashboardDebugSession);
        await clock.tickAsync(2000);
        await stopPromise;

        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);

        resolveStartDebugging?.(true);
        await openPromise;
    });

    test('a dashboard that starts after shutdown retries a failed background stop', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        }));
        let dashboardStopAttempts = 0;
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === dashboardDebugSession && dashboardStopAttempts++ === 0) {
                throw new Error('Late dashboard stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        const stopPromise = aspireDebugSession.stopDebugging();
        await clock.tickAsync(2000);
        await stopPromise;

        startSessionCallback?.(dashboardDebugSession);
        await clock.tickAsync(0);

        assert.strictEqual(stopDebugging.callCount, 3);
        assert.strictEqual(stopDebugging.firstCall.args[0], parentDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.thirdCall.args[0], dashboardDebugSession);

        resolveStartDebugging?.(true);
        await openPromise;
    });

    test('stopDebugging treats dashboard termination during a pending stop as success', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.withArgs(dashboardDebugSession).returns(new Promise<void>(() => { }));
        stopDebugging.withArgs(parentDebugSession).resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        startSessionCallback?.(dashboardDebugSession);
        await openPromise;
        const stopPromise = aspireDebugSession.stopDebugging();
        terminateSessionCallback?.(dashboardDebugSession);
        await stopPromise;

        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);
    });

    test('finalizeForExtensionShutdown stops a tracked dashboard debug session', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: 'Aspire Dashboard',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession = dashboardDebugSession;

        aspireDebugSession.finalizeForExtensionShutdown();
        await Promise.resolve();

        sinon.assert.calledOnceWithExactly(stopDebugging, dashboardDebugSession);
    });

    test('launching the dashboard browser ignores a dashboard session belonging to another Aspire session', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const foreignDashboardSession = {
            id: 'foreign-dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: { id: 'other-aspire-session' },
        } as unknown as vscode.DebugSession;
        const ownDashboardSession = {
            id: 'own-dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let listenerDisposed = false;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            // Mirror VS Code: once the listener is disposed it stops receiving events. Without
            // this, a listener that wrongly consumed a foreign session would still observe the
            // session it was supposed to capture and hide the bug.
            startSessionCallback = session => {
                if (!listenerDisposed) {
                    callback(session);
                }
            };
            return { dispose: () => { listenerDisposed = true; } };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        startSessionCallback?.(foreignDashboardSession);
        const trackedAfterForeignSession = (aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession;
        startSessionCallback?.(ownDashboardSession);
        await openPromise;

        assert.strictEqual(trackedAfterForeignSession, null);
        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession, ownDashboardSession);
    });

    test('launching the dashboard browser stops a session that starts after the Aspire session was disposed', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        }));
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        aspireDebugSession.dispose();
        startSessionCallback?.(dashboardDebugSession);
        resolveStartDebugging?.(true);
        await openPromise;
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebugging.calledWith(dashboardDebugSession), true);
        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher._dashboardDebugSession, null);
    });

    test('launching the dashboard browser does not fall back to an external browser after the Aspire session was disposed', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(() => ({ dispose: sinon.stub() }));
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        }));
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const openExternal = sinon.stub(vscode.env, 'openExternal').resolves(true);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const openPromise = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        await Promise.resolve();
        aspireDebugSession.dispose();
        resolveStartDebugging?.(false);
        await openPromise;

        assert.strictEqual(openExternal.called, false);
    });

    test('stopDebugging stops resource sessions before the AppHost and Aspire parent sessions', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'pwa-node',
            name: 'Node.js: app.js',
            configuration: {
                type: 'pwa-node',
                request: 'launch',
                name: 'Node.js: app.js',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const appHostSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._appHostDebugSession = appHostSession;
        (aspireDebugSession as any)._resourceDebugSessions = [
            appHostSession,
            {
                id: resourceDebugSession.id,
                session: resourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(resourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebuggingStub.callCount, 3);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], resourceDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.thirdCall.args[0], parentDebugSession);
    });

    test('stopDebugging waits for every resource stop to settle before stopping the AppHost', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { type: 'coreclr', request: 'launch', name: 'AppHost' },
        };
        const failingResourceDebugSession = {
            id: 'failing-resource-session',
            type: 'pwa-node',
            name: 'Node.js: broken.js',
            configuration: { type: 'pwa-node', request: 'launch', name: 'Node.js: broken.js' },
        };
        const slowResourceDebugSession = {
            id: 'slow-resource-session',
            type: 'pwa-chrome',
            name: 'Browser: http://localhost:5173',
            configuration: { type: 'pwa-chrome', request: 'launch', name: 'Browser: http://localhost:5173' },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };

        // The slow resource models an adapter that has acknowledged the stop but has not finished
        // tearing its process down yet. It is released on a timer rather than from inside the
        // AppHost stop so the ordering is a property of stopDebugging, not of the fake.
        let releaseSlowResourceStop!: () => void;
        const slowResourceStopGate = new Promise<void>(resolve => { releaseSlowResourceStop = resolve; });
        const stopOrder: string[] = [];

        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            stopOrder.push((session as unknown as { id: string }).id);
            if (session === (failingResourceDebugSession as unknown as vscode.DebugSession)) {
                throw new Error('Resource stop failed');
            }

            if (session === (slowResourceDebugSession as unknown as vscode.DebugSession)) {
                await slowResourceStopGate;
                stopOrder.push('slow-resource-session-settled');
            }
        });

        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: failingResourceDebugSession.id,
                session: failingResourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(failingResourceDebugSession as unknown as vscode.DebugSession),
            },
            {
                id: slowResourceDebugSession.id,
                session: slowResourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(slowResourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        const stopPromise = aspireDebugSession.stopDebugging();
        const releaseTimer = setTimeout(releaseSlowResourceStop, 25);

        try {
            // The rejection from the first resource must still reach the caller. Losing it would
            // report a clean shutdown for a session that left a debugger attached.
            await assert.rejects(() => stopPromise, /Resource stop failed/);
        }
        finally {
            clearTimeout(releaseTimer);
            releaseSlowResourceStop();
        }

        // Every resource has to reach a settled state before the AppHost stop starts, whether it
        // succeeded or failed. That ordering is the point of the method, and it is most load-bearing
        // exactly here, on the path where a failing resource would otherwise be left orphaned.
        assert.deepStrictEqual(stopOrder, [
            'failing-resource-session',
            'slow-resource-session',
            'slow-resource-session-settled',
            'apphost-session',
            'aspire-session',
        ]);
        assert.strictEqual(stopDebuggingStub.callCount, 4);
    });

    test('stopDebugging still stops the Aspire parent session when AppHost stop fails', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging')
            .callsFake(async session => {
                if (session === appHostDebugSession) {
                    throw new Error('AppHost stop failed');
                }
            });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /AppHost stop failed/);

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
    });

    test('stopDebugging reports both resource and AppHost stop failures', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'pwa-node',
            name: 'Node.js: server.js',
            configuration: {
                type: 'pwa-node',
                request: 'launch',
                name: 'Node.js: server.js',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const resourceStopFailure = new Error('Resource stop failed');
        const appHostStopFailure = new Error('AppHost stop failed');
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging')
            .callsFake(async session => {
                if (session === resourceDebugSession) {
                    throw resourceStopFailure;
                }

                if (session === appHostDebugSession) {
                    throw appHostStopFailure;
                }
            });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: resourceDebugSession.id,
                session: resourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(resourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        await assert.rejects(
            () => aspireDebugSession.stopDebugging(),
            (error: unknown) => {
                assert.ok(error instanceof AggregateError);
                assert.deepStrictEqual((error as AggregateError).errors, [resourceStopFailure, appHostStopFailure]);
                // The RPC boundary logs and shows err.message alone, so the reasons have to be in
                // the message too or the caller learns only that something failed.
                assert.ok(
                    error.message.includes(resourceStopFailure.message) && error.message.includes(appHostStopFailure.message),
                    `The aggregate message must name every reason, but was: ${error.message}`);
                return true;
            });

        assert.strictEqual(stopDebuggingStub.callCount, 3);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], resourceDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.thirdCall.args[0], parentDebugSession);
    });

    test('stopDebugging stops the remaining sessions when a resource stopSession throws synchronously', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const healthyResourceDebugSession = {
            id: 'healthy-resource-session',
            type: 'pwa-node',
            name: 'Node.js: server.js',
            configuration: {
                type: 'pwa-node',
                request: 'launch',
                name: 'Node.js: server.js',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const synchronousStopFailure = new Error('Synchronous resource stop failed');
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                // stopSession() is only typed as returning a Thenable, so a resource debugger
                // extension is free to throw before it ever produces one. Ordered first so a
                // regression that lets the throw escape the promise-array construction would
                // abort the shutdown before any other session is stopped.
                id: 'throwing-resource-session',
                session: { id: 'throwing-resource-session' } as unknown as vscode.DebugSession,
                stopSession: () => {
                    throw synchronousStopFailure;
                },
            },
            {
                id: healthyResourceDebugSession.id,
                session: healthyResourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(healthyResourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        await assert.rejects(
            () => aspireDebugSession.stopDebugging(),
            (error: unknown) => {
                assert.strictEqual(error, synchronousStopFailure);
                return true;
            });

        assert.deepStrictEqual(
            stopDebuggingStub.getCalls().map(call => call.args[0]),
            [
                healthyResourceDebugSession as unknown as vscode.DebugSession,
                appHostDebugSession as unknown as vscode.DebugSession,
                parentDebugSession as unknown as vscode.DebugSession,
            ]);
        assert.strictEqual((aspireDebugSession as any)._disposed, false, 'A failed session must remain available for retry');
    });

    // The synthetic Aspire parent is the last session the shutdown stops, and its failure is part
    // of the same contract as the resource and AppHost failures: reported, not swallowed.
    test('stopDebugging rethrows an Aspire parent stop failure on its own', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { type: 'coreclr', request: 'launch', name: 'AppHost' },
        };
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const parentStopFailure = new Error('Aspire parent stop failed');
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging')
            .callsFake(async session => {
                if (session === parentDebugSession) {
                    throw parentStopFailure;
                }
            });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };

        await assert.rejects(
            () => aspireDebugSession.stopDebugging(),
            (error: unknown) => {
                assert.strictEqual(error, parentStopFailure);
                return true;
            });

        assert.deepStrictEqual(
            stopDebuggingStub.getCalls().map(call => call.args[0]),
            [
                appHostDebugSession as unknown as vscode.DebugSession,
                parentDebugSession as unknown as vscode.DebugSession,
            ]);
        assert.strictEqual((aspireDebugSession as any)._disposed, false, 'A failed session must remain available for retry');
    });

    // stopAllSessions() snapshots the resource list before its awaits, so a resource that starts
    // mid-shutdown must not be registered as an ordinary session: it would miss the snapshot and be
    // stopped only by dispose(), after the AppHost and Aspire parent had already been stopped.
    test('stopDebugging awaits and reports a resource session that starts mid-shutdown', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { type: 'coreclr', request: 'launch', name: 'AppHost' },
        };
        const snapshotResourceDebugSession = {
            id: 'snapshot-resource-session',
            configuration: { type: 'pwa-node', request: 'launch', name: 'Node.js: server.js' },
        };
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stopOrder: string[] = [];
        let releaseSnapshotResourceStop: (() => void) | undefined;
        const snapshotResourceStopGate = new Promise<void>(resolve => { releaseSnapshotResourceStop = resolve; });
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === snapshotResourceDebugSession) {
                await snapshotResourceStopGate;
            }

            stopOrder.push((session as unknown as { id: string }).id);
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: snapshotResourceDebugSession.id,
                session: snapshotResourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(snapshotResourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        const stopPromise = aspireDebugSession.stopDebugging();
        await Promise.resolve();

        // The snapshot resource stop is still in flight, so the snapshot has already been taken and
        // the AppHost stop has not started.
        let rejectLateResourceStop: ((reason: Error) => void) | undefined;
        const lateResourceStopGate = new Promise<void>((_, reject) => { rejectLateResourceStop = reject; });
        const lateStopSession = sinon.stub().callsFake(() => {
            stopOrder.push('late-resource-stop-started');
            return lateResourceStopGate;
        });
        const lateSession = {
            id: 'late-resource-session',
            processId: 4321,
            session: { id: 'late-resource-session' } as unknown as vscode.DebugSession,
            stopSession: lateStopSession,
            termination: new Promise<number>(() => { }),
        };
        const tracked = aspireDebugSession.trackAlreadyStartedResourceSession(
            { type: 'node', request: 'launch', name: 'late', runId: 'late-run', debugSessionId: null } as any,
            lateSession as any);

        assert.strictEqual(tracked, undefined, 'A session started during shutdown must not be tracked');
        assert.strictEqual(lateStopSession.callCount, 1, 'A session started during shutdown must be stopped immediately');
        assert.strictEqual(
            (aspireDebugSession as any)._resourceDebugSessions.includes(lateSession),
            false,
            'A session started during shutdown must not be registered behind the snapshot');

        releaseSnapshotResourceStop!();
        await new Promise(resolve => setImmediate(resolve));
        const appHostStartedBeforeLateStopSettled = stopOrder.includes('apphost-session');
        rejectLateResourceStop!(new Error('late resource stop failed'));
        await assert.rejects(stopPromise, /late resource stop failed/);

        assert.strictEqual(
            appHostStartedBeforeLateStopSettled,
            false,
            'The AppHost must not stop while a late resource stop is still pending');
        assert.deepStrictEqual(stopOrder, [
            'late-resource-stop-started',
            'snapshot-resource-session',
            'apphost-session',
            'aspire-session',
        ]);
    });

    test('stopDebugging cancels pending launch work before awaiting its completion', async () => {
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
        const terminalProvider = { isDebugConfigEnvironmentLoggingEnabled: () => false };
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });
        const pendingStart = aspireDebugSession.beginPendingDebugSessionStart('rust');
        const cancelPendingBuild = sinon.stub().callsFake(() => pendingStart.dispose());

        aspireDebugSession.registerPendingStartCancellation({ dispose: cancelPendingBuild });
        const shutdown = aspireDebugSession.stopDebugging();

        assert.strictEqual(cancelPendingBuild.callCount, 1);
        await shutdown;
        assert.strictEqual(stopDebugging.callCount, 1);
    });

    test('stopDebugging awaits a resource start event that arrives after the original session snapshot', async () => {
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
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'node',
            name: 'Late resource',
            configuration: {
                runId: 'resource-run',
            },
        };
        const debugConfig = {
            runId: 'resource-run',
            debugSessionId: 'debug-1',
            type: 'node',
            name: 'Late resource',
            request: 'launch',
            program: '/workspace/app.js',
            cwd: '/workspace',
        } as AspireResourceExtendedDebugConfiguration;
        const terminalProvider = { isDebugConfigEnvironmentLoggingEnabled: () => false };
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const lateStopFailure = new Error('Late resource stop failed');
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(session => {
            if (session?.id === resourceDebugSession.id) {
                return Promise.reject(lateStopFailure);
            }

            return Promise.resolve();
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const resourceStart = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        const shutdown = aspireDebugSession.stopDebugging();
        const shutdownState = await Promise.race([
            shutdown.then(() => 'completed' as const, () => 'failed' as const),
            new Promise<'pending'>(resolve => setImmediate(() => resolve('pending'))),
        ]);

        assert.strictEqual(shutdownState, 'pending', 'Shutdown must retain ownership of an accepted resource start');

        startSessionCallback?.(resourceDebugSession as unknown as vscode.DebugSession);

        await resourceStart;
        await assert.rejects(shutdown, error => error === lateStopFailure);
    });

    test('stopDebugging bounds a wedged resource start and stops the session if it later starts', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let resolveStart: ((started: boolean) => void) | undefined;
        const startRequest = new Promise<boolean>(resolve => {
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
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'node',
            name: 'Resource',
            configuration: {
                runId: 'resource-run',
            },
        };
        const debugConfig = {
            runId: 'resource-run',
            debugSessionId: 'debug-1',
            type: 'node',
            name: 'Resource',
            request: 'launch',
            program: '/workspace/app.js',
            cwd: '/workspace',
        } as AspireResourceExtendedDebugConfiguration;
        const terminalProvider = { isDebugConfigEnvironmentLoggingEnabled: () => false };
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').returns(startRequest);
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const resourceStart = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        const shutdown = aspireDebugSession.stopDebugging();
        await clock.tickAsync(10_001);

        await assert.rejects(shutdown, /Timed out after 10 seconds waiting for debug session 'Resource' to start/);

        resolveStart!(true);
        startSessionCallback?.(resourceDebugSession as unknown as vscode.DebugSession);
        await resourceStart;
        await Promise.resolve();

        assert.strictEqual(
            stopDebuggingStub.calledWith(resourceDebugSession as unknown as vscode.DebugSession),
            true,
            'A resource accepted after the shutdown deadline must still be stopped immediately');
        clock.restore();
    });

    // The AppHost process exiting disposes this session, so a disposal can land while the CLI's
    // ordered shutdown is still in flight. Disposal must not fire the owned-session stop callbacks
    // behind its back: that stops every resource a second time and lets the AppHost stop start
    // before a resource stop has finished.
    test('disposal while a shutdown is in flight leaves session stopping to the shutdown', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const events: string[] = [];
        let releaseResourceStop: (() => void) | undefined;
        const resourceStopGate = new Promise<void>(resolve => { releaseResourceStop = resolve; });
        const resourceStop = sinon.stub().callsFake(async () => {
            events.push('resource-stop-started');
            await resourceStopGate;
            events.push('resource-stop-finished');
        });
        const appHostStop = sinon.stub().callsFake(async () => { events.push('apphost-stop'); });
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(async () => { });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const resourceDebugSession = {
            id: 'resource-session',
            session: { id: 'resource-session' } as unknown as vscode.DebugSession,
            stopSession: resourceStop,
        };
        (aspireDebugSession as any)._resourceDebugSessions = [resourceDebugSession];
        (aspireDebugSession as any)._appHostDebugSession = {
            id: 'apphost-session',
            session: { id: 'apphost-session' } as unknown as vscode.DebugSession,
            stopSession: appHostStop,
        };

        const shutdown = aspireDebugSession.stopDebugging();
        await new Promise(resolve => setTimeout(resolve, 0));

        // This is VS Code tearing down the inline adapter, or the AppHost exit handler, while the
        // ordered shutdown is still running.
        aspireDebugSession.dispose();

        assert.deepStrictEqual(events, ['resource-stop-started'], 'Disposal must not stop the AppHost while a resource stop is still in flight');

        releaseResourceStop!();
        await shutdown;

        assert.deepStrictEqual(
            events,
            ['resource-stop-started', 'resource-stop-finished', 'apphost-stop'],
            'The shutdown must keep the resources-before-AppHost ordering across a concurrent disposal');
        assert.strictEqual(resourceStop.callCount, 1, 'The resource must be stopped once, by the shutdown, not again by disposal');
    });

    // Two overlapping stop requests must not both run the ordered shutdown: every session would be
    // stopped twice, and one caller could be told the shutdown succeeded while the other was told
    // it failed.
    test('overlapping stopDebugging calls share one shutdown and one result', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const resourceStopFailure = new Error('Resource stop failed');
        let releaseResourceStop: (() => void) | undefined;
        const resourceStopGate = new Promise<void>(resolve => { releaseResourceStop = resolve; });
        const stopSession = sinon.stub().callsFake(async () => {
            await resourceStopGate;
            throw resourceStopFailure;
        });
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(async () => { });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: 'resource-session',
                session: { id: 'resource-session' } as unknown as vscode.DebugSession,
                stopSession,
            },
        ];

        const first = aspireDebugSession.stopDebugging();
        const second = aspireDebugSession.stopDebugging();

        assert.strictEqual(first, second, 'Overlapping stop requests must share the same shutdown promise');

        releaseResourceStop!();

        const failures = await Promise.allSettled([first, second]);

        assert.deepStrictEqual(
            failures.map(result => result.status),
            ['rejected', 'rejected'],
            'Both callers must see the same failed shutdown');
        assert.strictEqual((failures[0] as PromiseRejectedResult).reason, resourceStopFailure);
        assert.strictEqual((failures[1] as PromiseRejectedResult).reason, resourceStopFailure);
        assert.strictEqual(stopSession.callCount, 1, 'The ordered shutdown must run once, not once per caller');
    });

    test('a synchronous reentrant stopDebugging call joins the current shutdown', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        let reentrantStop: Promise<void> | undefined;
        const stopSession = sinon.stub().callsFake(() => {
            if (stopSession.callCount === 1) {
                reentrantStop = aspireDebugSession.stopDebugging();
            }

            return Promise.resolve();
        });
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: 'resource-session',
                session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession,
                stopSession,
            },
        ];

        const firstStop = aspireDebugSession.stopDebugging();
        await firstStop;

        assert.strictEqual(reentrantStop, firstStop, 'A synchronous reentrant caller must receive the published shutdown promise');
        assert.strictEqual(stopSession.callCount, 1, 'Reentrancy must not start a second ordered shutdown');
    });

    // The shutdown is reachable from the CLI's AppDomain.ProcessExit handler, which blocks the
    // exiting process on the RPC call with CancellationToken.None. vscode.debug.stopDebugging()
    // only resolves once the adapter acknowledges, so an unbounded wait on one wedged adapter hangs
    // the CLI's exit forever.
    test('stopDebugging gives up on a wedged resource stop instead of waiting forever', async () => {
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
        const appHostDebugSession = { id: 'apphost-session', name: 'AppHost' };
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        // Never settles, modelling an adapter that accepted the stop and then wedged.
        const wedgedStop = sinon.stub().returns(new Promise<void>(() => { }));
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const appHostSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._appHostDebugSession = appHostSession;
        (aspireDebugSession as any)._resourceDebugSessions = [
            appHostSession,
            {
                id: 'resource-session',
                session: { id: 'resource-session', name: 'Wedged resource' } as unknown as vscode.DebugSession,
                stopSession: wedgedStop,
            },
        ];

        const stopPromise = aspireDebugSession.stopDebugging();
        // Just short of the budget the shutdown is still waiting on the resource, so nothing else
        // has been stopped yet: the ordering is honoured right up to the deadline.
        await clock.tickAsync(9_000);
        assert.strictEqual(stopDebuggingStub.callCount, 0, 'The AppHost must not be stopped while a resource stop is still within budget');

        await clock.tickAsync(2_000);

        await assert.rejects(stopPromise, (err: Error) => {
            assert.strictEqual(err.message, debugSessionStopTimedOut('Wedged resource', 10));
            return true;
        });
        // Giving up on the resource must not abandon the rest of the shutdown - the AppHost and the
        // Aspire parent are still stopped, in that order.
        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
        clock.restore();
    });

    test('a timed-out stop reports the remaining shutdown budget', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const appHostSession = {
            id: 'apphost-session',
            session: { id: 'apphost-session', name: 'AppHost' } as unknown as vscode.DebugSession,
            stopSession: () => new Promise<void>(() => { }),
        };
        (aspireDebugSession as any)._appHostDebugSession = appHostSession;
        (aspireDebugSession as any)._resourceDebugSessions = [
            appHostSession,
            {
                id: 'slow-resource',
                session: { id: 'slow-resource', name: 'Slow resource' } as unknown as vscode.DebugSession,
                stopSession: () => new Promise<void>(resolve => setTimeout(resolve, 9_000)),
            },
        ];

        const stopPromise = aspireDebugSession.stopDebugging();
        await clock.tickAsync(10_001);

        await assert.rejects(stopPromise, (err: Error) => {
            assert.strictEqual(err.message, debugSessionStopTimedOut('AppHost', 1));
            return true;
        });
        clock.restore();
    });

    // The DAP disconnect/terminate request is the dominant user Stop path - the toolbar's red
    // square, "Stop All Sessions", and window close all arrive here - so it has to run the
    // ordered shutdown without waiting to answer the request.
    test('a DAP disconnect request runs the ordered shutdown rather than disposing', async () => {
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
        const appHostDebugSession = { id: 'apphost-session', name: 'AppHost' };
        const resourceDebugSession = { id: 'resource-session', name: 'Node.js: app.js' };
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const appHostSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };
        (aspireDebugSession as any)._appHostDebugSession = appHostSession;
        (aspireDebugSession as any)._resourceDebugSessions = [
            appHostSession,
            {
                id: resourceDebugSession.id,
                session: resourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(resourceDebugSession as unknown as vscode.DebugSession),
            },
        ];
        const sentMessages: any[] = [];
        aspireDebugSession.onDidSendMessage(message => sentMessages.push(message));

        aspireDebugSession.handleMessage({ command: 'disconnect', seq: 7 });
        await (aspireDebugSession as any)._stopPromise;

        assert.strictEqual(stopDebuggingStub.callCount, 3);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], resourceDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.thirdCall.args[0], parentDebugSession);

        // The shutdown stops the synthetic Aspire parent, which makes VS Code send this same
        // disconnect request back. Exactly one response has to go out, and it cannot wait for the
        // shutdown that is waiting on it.
        const responses = sentMessages.filter(message => message.type === 'response' && message.command === 'disconnect');
        assert.strictEqual(responses.length, 1, 'A disconnect request must be answered exactly once');
        assert.strictEqual(responses[0].request_seq, 7);
        assert.strictEqual(responses[0].success, true);
    });

    // A re-entrant disconnect is the normal case, not an edge case: stopping the Aspire parent is
    // the last step of the shutdown and makes VS Code disconnect this adapter. That second entry
    // must join the in-flight shutdown rather than start a second one.
    test('a disconnect delivered while a shutdown is in flight joins it instead of starting another', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        let releaseResourceStop: (() => void) | undefined;
        const resourceStopGate = new Promise<void>(resolve => { releaseResourceStop = resolve; });
        const stopSession = sinon.stub().callsFake(() => resourceStopGate);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: 'resource-session',
                session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession,
                stopSession,
            },
        ];

        const stopPromise = aspireDebugSession.stopDebugging();
        aspireDebugSession.handleMessage({ command: 'disconnect', seq: 3 });

        releaseResourceStop!();
        await stopPromise;

        assert.strictEqual(stopSession.callCount, 1, 'The re-entrant disconnect must not run a second shutdown');
    });

    // Caching a rejected shutdown forever would make every later attempt replay the original
    // failure without retrying, leaving the sessions that failed to stop running with no way to
    // ask again.
    test('a failed shutdown can be retried and only targets what is still running', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stoppedResourceStop = sinon.stub().resolves();
        const failingResourceStop = sinon.stub().rejects(new Error('Resource stop failed'));
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [
            { id: 'stopped-resource', session: { id: 'stopped-resource', name: 'Stopped' } as unknown as vscode.DebugSession, stopSession: stoppedResourceStop },
            { id: 'failing-resource', session: { id: 'failing-resource', name: 'Failing' } as unknown as vscode.DebugSession, stopSession: failingResourceStop },
        ];

        await assert.rejects(aspireDebugSession.stopDebugging(), /Resource stop failed/);

        failingResourceStop.resetBehavior();
        failingResourceStop.resolves();

        await aspireDebugSession.stopDebugging();

        assert.strictEqual(failingResourceStop.callCount, 2, 'The session that did not stop must be asked again');
        assert.strictEqual(stoppedResourceStop.callCount, 1, 'A session that already stopped must not be stopped again by the retry');
    });

    test('a timed-out VS Code resource stop issues a fresh request on retry', async () => {
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
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'node',
            name: 'Resource',
            configuration: {
                runId: 'resource-run',
            },
        };
        const debugConfig = {
            runId: 'resource-run',
            debugSessionId: 'debug-1',
            type: 'node',
            name: 'Resource',
            request: 'launch',
            program: '/workspace/app.js',
            cwd: '/workspace',
        } as AspireResourceExtendedDebugConfiguration;
        const terminalProvider = { isDebugConfigEnvironmentLoggingEnabled: () => false };
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        let resourceStopAttempts = 0;
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(session => {
            if (session?.id === resourceDebugSession.id) {
                resourceStopAttempts++;
                return resourceStopAttempts === 1
                    ? new Promise<void>(() => { })
                    : Promise.resolve();
            }

            return Promise.resolve();
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        startSessionCallback?.(resourceDebugSession as unknown as vscode.DebugSession);
        await sessionPromise;

        const firstStop = aspireDebugSession.stopDebugging();
        await clock.tickAsync(10_001);
        await assert.rejects(firstStop, /Timed out after 10 seconds waiting for debug session 'Resource' to stop/);

        const retry = aspireDebugSession.stopDebugging();
        await Promise.resolve();
        await Promise.resolve();

        assert.strictEqual(resourceStopAttempts, 2, 'The retry must issue a new vscode.debug.stopDebugging request');
        await retry;
        clock.restore();
    });

    test('a late resource that fails to stop between shutdown attempts is retried by the next attempt', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stopOrder: string[] = [];
        let parentStopAttempts = 0;
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            assert.strictEqual(session, parentDebugSession);
            parentStopAttempts++;
            stopOrder.push(`parent-${parentStopAttempts}`);
            if (parentStopAttempts === 1) {
                throw new Error('Parent stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /Parent stop failed/);
        await Promise.resolve();

        let rejectImmediateStop: ((error: Error) => void) | undefined;
        const immediateStop = new Promise<void>((_, reject) => {
            rejectImmediateStop = reject;
        });
        let lateStopAttempts = 0;
        const lateStopSession = sinon.stub().callsFake(() => {
            lateStopAttempts++;
            stopOrder.push(`late-${lateStopAttempts}`);
            return lateStopAttempts === 1 ? immediateStop : Promise.resolve();
        });
        const lateSession = {
            id: 'late-resource-session',
            processId: 4321,
            session: { id: 'late-resource-session', name: 'Late resource' } as unknown as vscode.DebugSession,
            stopSession: lateStopSession,
            termination: new Promise<number>(() => { }),
        };

        const tracked = aspireDebugSession.trackAlreadyStartedResourceSession(
            { type: 'node', request: 'launch', name: 'late', runId: 'late-run', debugSessionId: null } as any,
            lateSession as any);

        assert.strictEqual(tracked, undefined);
        assert.strictEqual(lateStopSession.callCount, 1, 'The late session must be stopped immediately');

        rejectImmediateStop!(new Error('Immediate late stop failed'));
        await immediateStop.catch(() => undefined);
        await Promise.resolve();

        await aspireDebugSession.stopDebugging();

        assert.strictEqual(lateStopSession.callCount, 2, 'The next shutdown must retry the failed late session');
        assert.strictEqual(parentStopAttempts, 2);
        assert.deepStrictEqual(stopOrder, ['parent-1', 'late-1', 'late-2', 'parent-2']);
        assert.strictEqual((aspireDebugSession as any)._disposed, true);
    });

    test('a failed resource stop created by startAndGetDebugSession is retried through VS Code', async () => {
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
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'retry-run',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'API',
            request: 'launch',
            program: '/workspace/Api/Api.dll',
            cwd: '/workspace/Api',
        } as AspireResourceExtendedDebugConfiguration;
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'coreclr',
            name: 'API',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async () => {
            startSessionCallback?.(resourceDebugSession);
            return true;
        });
        const resourceStopFailure = new Error('Resource stop failed');
        let resourceStopAttempts = 0;
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === resourceDebugSession) {
                resourceStopAttempts++;
                if (resourceStopAttempts === 1) {
                    throw resourceStopFailure;
                }
            }
        });
        let cleanupCalls = 0;
        registerRunCleanup(debugConfig.runId, () => {
            cleanupCalls++;
        });
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });

        const trackedSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.strictEqual(trackedSession?.id, resourceDebugSession.id);
        await assert.rejects(
            () => aspireDebugSession.stopDebugging(),
            (error: unknown) => {
                assert.strictEqual(error, resourceStopFailure);
                return true;
            });

        await aspireDebugSession.stopDebugging();

        assert.deepStrictEqual(
            stopDebuggingStub.getCalls().map(call => call.args[0]),
            [
                resourceDebugSession,
                parentDebugSession as unknown as vscode.DebugSession,
                resourceDebugSession,
            ]);
        assert.strictEqual(resourceStopAttempts, 2);
        assert.strictEqual(cleanupCalls, 1, 'Run cleanup must not repeat when the stop request is retried');
    });

    test('a naturally terminated resource is removed before a failed shutdown retry', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        const terminateSessionCallbacks: ((session: vscode.DebugSession) => unknown)[] = [];
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
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'naturally-terminated-run',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'API',
            request: 'launch',
            program: '/workspace/Api/Api.dll',
            cwd: '/workspace/Api',
        } as AspireResourceExtendedDebugConfiguration;
        const resourceDebugSession = {
            id: 'resource-session',
            type: 'coreclr',
            name: 'API',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateSessionCallbacks.push(callback);
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async () => {
            startSessionCallback?.(resourceDebugSession);
            return true;
        });
        const resourceStopFailure = new Error('Resource stop failed');
        const alreadyTerminatedFailure = new Error('Resource debug session already terminated');
        let resourceStopAttempts = 0;
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === resourceDebugSession) {
                resourceStopAttempts++;
                throw resourceStopAttempts === 1 ? resourceStopFailure : alreadyTerminatedFailure;
            }
        });
        let removalCalls = 0;
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => {
                removalCalls++;
            });

        const trackedSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.strictEqual(trackedSession?.id, resourceDebugSession.id);
        await assert.rejects(
            () => aspireDebugSession.stopDebugging(),
            (error: unknown) => {
                assert.strictEqual(error, resourceStopFailure);
                return true;
            });

        for (const terminateSessionCallback of terminateSessionCallbacks) {
            terminateSessionCallback(resourceDebugSession);
        }
        await Promise.resolve();

        await aspireDebugSession.stopDebugging();

        assert.deepStrictEqual(
            stopDebuggingStub.getCalls().map(call => call.args[0]),
            [
                resourceDebugSession,
                parentDebugSession as unknown as vscode.DebugSession,
            ]);
        assert.strictEqual(resourceStopAttempts, 1, 'A naturally terminated resource must not be stopped again');
        assert.strictEqual((aspireDebugSession as any)._resourceDebugSessions.length, 0);
        assert.strictEqual((aspireDebugSession as any)._disposed, true);
        assert.strictEqual(removalCalls, 1);
    });

    // Once a shutdown has succeeded there is nothing left to order, so repeat callers - the CLI RPC
    // endpoint and the DAP disconnect that VS Code sends after the parent stops - must be no-ops
    // rather than re-stopping sessions that are already gone.
    test('a second stopDebugging after a successful shutdown does not stop anything again', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stopSession = sinon.stub().resolves();
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [
            { id: 'resource-session', session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession, stopSession },
        ];

        await aspireDebugSession.stopDebugging();
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopSession.callCount, 1);
        assert.strictEqual(stopDebuggingStub.callCount, 1, 'Only the Aspire parent stop, and only once');
    });

    // dispose() enters the same ordered shutdown, so a later stopDebugging() call joins the
    // completed operation rather than stopping the same sessions again.
    test('stopDebugging after a plain disposal does not re-stop the disposed sessions', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const stopSession = sinon.stub().resolves();
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [
            { id: 'resource-session', session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession, stopSession },
        ];

        aspireDebugSession.dispose();
        await (aspireDebugSession as any)._stopPromise;

        const stopsAfterDisposal = stopSession.callCount;
        const parentStopsAfterDisposal = stopDebuggingStub.callCount;

        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopSession.callCount, stopsAfterDisposal, 'Disposal already asked the session to stop');
        assert.strictEqual(stopDebuggingStub.callCount, parentStopsAfterDisposal, 'The Aspire parent must not be stopped a second time');
    });

    test('a plain disposal stops resources before the AppHost exactly once', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = { isDebugConfigEnvironmentLoggingEnabled: () => false };
        const dcpServer = { sendNotification: sinon.stub() };
        const stopOrder: string[] = [];
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            assert.ok(session);
            stopOrder.push(session.name);
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });

        const ownedSessions = ['AppHost', 'Frontend', 'Cache'].map(name => {
            const stopSession = sinon.stub().callsFake(async () => {
                stopOrder.push(name);
            });
            const trackedSession = aspireDebugSession.trackAlreadyStartedResourceSession(
                { runId: `run-${name}`, debugSessionId: `debug-${name}`, type: 'coreclr', name, request: 'launch' } as AspireResourceExtendedDebugConfiguration,
                {
                    id: `run-${name}`,
                    processId: 100,
                    session: { id: `run-${name}`, name } as vscode.DebugSession,
                    stopSession,
                    termination: new Promise<number>(() => { }),
                });
            return { name, stopSession, trackedSession };
        });
        (aspireDebugSession as any)._appHostDebugSession = ownedSessions[0].trackedSession;

        aspireDebugSession.dispose();
        await (aspireDebugSession as any)._stopPromise;

        for (const { name, stopSession } of ownedSessions) {
            assert.strictEqual(stopSession.callCount, 1, `${name} must be stopped exactly once by a plain disposal`);
        }

        assert.deepStrictEqual(stopOrder, ['Frontend', 'Cache', 'AppHost', 'Aspire']);
        assert.deepStrictEqual(stopDebuggingStub.args, [[parentDebugSession]], 'The Aspire parent is stopped exactly once');

        // dispose() is reachable more than once - VS Code disposes the adapter and the extension
        // disposes it again on deactivate - and the repeat must not re-stop anything.
        aspireDebugSession.dispose();
        await (aspireDebugSession as any)._stopPromise;

        assert.deepStrictEqual(
            ownedSessions.map(ownedSession => ownedSession.stopSession.callCount),
            [1, 1, 1],
            'A repeated disposal must not stop the sessions again');
        assert.strictEqual(stopDebuggingStub.callCount, 1, 'A repeated disposal must not stop the Aspire parent again');
    });

    test('a failed background shutdown still starts CLI and process-tree cleanup', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const expectedError = new Error('resource stop failed');
        const stopCli = sinon.stub().resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            { isCliDebugLoggingEnabled: () => false } as any,
            () => { });
        (aspireDebugSession as any)._rpcClient = { stopCli };
        (aspireDebugSession as any)._resourceDebugSessions = [
            {
                id: 'resource-session',
                session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession,
                stopSession: sinon.stub().rejects(expectedError),
            },
        ];
        const scheduleCliProcessTermination = sinon.stub(aspireDebugSession as any, 'scheduleCliProcessTermination');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        aspireDebugSession.dispose();
        await assert.rejects((aspireDebugSession as any)._stopPromise, error => error === expectedError);
        await new Promise(resolve => setImmediate(resolve));

        sinon.assert.calledOnce(stopCli);
        sinon.assert.calledOnce(scheduleCliProcessTermination);
        assert.strictEqual(aspireDebugSession.isDisposed, false, 'The failed ordered shutdown remains explicitly retryable');
    });

    // A stop for a session VS Code no longer knows about rejects, and these call sites cannot await
    // it: the late-start handlers return void and dispose() satisfies the Disposable contract. The
    // rejection has to be observed, or it surfaces as an unhandled rejection in the extension host
    // naming no session at all.
    test('a rejected stop on the late-start path does not raise an unhandled rejection', async () => {
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
        const terminalProvider = { isCliDebugLoggingEnabled: () => false };
        const unhandledRejections: unknown[] = [];
        const onUnhandledRejection = (reason: unknown) => unhandledRejections.push(reason);
        process.on('unhandledRejection', onUnhandledRejection);
        try {
            const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
            (aspireDebugSession as any)._stopping = true;

            const result = aspireDebugSession.trackAlreadyStartedResourceSession(
                { runId: 'run-1', debugSessionId: 'debug-1', type: 'node', name: 'Late', request: 'launch' } as any,
                {
                    id: 'late-session',
                    session: { id: 'late-session', name: 'Late' } as unknown as vscode.DebugSession,
                    // A synchronous throw, which is what an extension that is already torn down
                    // does, and which a bare `.catch()` on the return value would miss entirely.
                    stopSession: () => { throw new Error('Session already gone'); },
                    processId: 1,
                    termination: new Promise<number>(() => { }),
                } as any);

            assert.strictEqual(result, undefined, 'A session handed over mid-shutdown is not tracked');

            // Two turns is enough for a rejection created synchronously above to be reported.
            await Promise.resolve();
            await new Promise(resolve => setImmediate(resolve));

            assert.deepStrictEqual(unhandledRejections, []);
        }
        finally {
            process.off('unhandledRejection', onUnhandledRejection);
        }
    });

    test('stopDebugging does not stop the Aspire parent session twice when AppHost stop disposes the Aspire session', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => {
                const stopAppHost = vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession);
                aspireDebugSession.dispose();
                return stopAppHost;
            },
        };

        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
    });

    test('stopDebugging does not stop the Aspire parent session twice when AppHost termination arrives after stopDebugging', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };

        await aspireDebugSession.stopDebugging();
        aspireDebugSession.dispose();

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
    });

    test('stopDebugging waits for the Aspire parent stop when AppHost stop disposes the Aspire session', async () => {
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                type: 'coreclr',
                request: 'launch',
                name: 'AppHost',
            },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        let resolveParentStop: (() => void) | undefined;
        const parentStopPromise = new Promise<void>(resolve => {
            resolveParentStop = resolve;
        });
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === parentDebugSession) {
                await parentStopPromise;
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => {
                const stopAppHost = vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession);
                aspireDebugSession.dispose();
                return stopAppHost;
            },
        };

        const stopDebugging = aspireDebugSession.stopDebugging();
        const resultBeforeParentStop = await Promise.race([
            stopDebugging.then(() => 'completed'),
            new Promise<'pending'>(resolve => setTimeout(() => resolve('pending'), 25)),
        ]);

        assert.strictEqual(resultBeforeParentStop, 'pending');

        resolveParentStop!();
        await stopDebugging;

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
    });

    test('stopDebugging does not stop the AppHost debug session twice when disposal follows AppHost termination', async () => {
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
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                runId: 'apphost-run',
            },
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'apphost-run',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'AppHost',
            request: 'launch',
            program: '/workspace/AppHost/bin/Debug/net10.0/AppHost.dll',
            cwd: '/workspace/AppHost',
            isApphost: true,
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        startSessionCallback?.(appHostDebugSession as unknown as vscode.DebugSession);
        const appHostSession = await sessionPromise;
        (aspireDebugSession as any)._appHostDebugSession = appHostSession;

        await aspireDebugSession.stopDebugging();
        aspireDebugSession.dispose();

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
    });

    test('tracks the extension-created AppHost child for lifecycle recovery', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.mts');
        writeFileSync(appHostPath, '');

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
        const childDebugSession = {
            id: 'apphost-session',
            session: { id: 'apphost-session', configuration: { noDebug: false } } as unknown as vscode.DebugSession,
            stopSession: sinon.stub().resolves(),
        };
        const trackAppHostDebugSession = sinon.spy();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { },
            trackAppHostDebugSession);
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(childDebugSession);

        await aspireDebugSession.startAppHost(appHostPath, [], [], true, { forceBuild: false });

        assert.strictEqual(trackAppHostDebugSession.calledOnceWithExactly(aspireDebugSession, appHostPath, childDebugSession), true);
    });

    test('launches a Rust AppHost with the Rust debugger', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.rs');
        writeFileSync(appHostPath, '');
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            extensionId === 'ms-vscode.cpptools' || extensionId === 'vadimcn.vscode-lldb'
                ? { id: extensionId } as vscode.Extension<unknown>
                : undefined);

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
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'lldb',
            request: 'launch',
            name: 'Rust AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        });
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            appHostPath,
            ['cargo', 'run', '--', '--example-argument'],
            [],
            true,
            { forceBuild: false });

        const launchConfig = createDebugSessionConfiguration.firstCall.args[1] as RustLaunchConfiguration;
        const appHostArgs = createDebugSessionConfiguration.firstCall.args[2];
        const debuggerExtension = createDebugSessionConfiguration.firstCall.args[5];

        assert.deepStrictEqual(launchConfig, {
            type: 'rust',
            working_directory: dirname(appHostPath),
        });
        assert.deepStrictEqual(appHostArgs, ['--example-argument']);
        assert.strictEqual(debuggerExtension.resourceType, 'rust');
    });

    test('an AppHost restart is aborted and forces CLI cleanup when resource shutdown fails', async () => {
        let restartHandler: ((debugSessionId: string) => boolean) | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => unknown) | undefined;
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
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {
                runId: 'apphost-run',
            },
        };
        const appHostResourceSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: sinon.stub().resolves(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            runId: 'apphost-run',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'AppHost',
            request: 'launch',
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore').callsFake((_debugAdapter, onRestart) => {
            restartHandler = onRestart;
        });
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostResourceSession);
        const cliStop = sinon.stub().resolves();
        (aspireDebugSession as any)._rpcClient = { stopCli: cliStop };
        const terminateCliProcessTree = sinon.stub(aspireDebugSession, 'terminateCliProcessTree');

        await aspireDebugSession.startAppHost('/workspace/AppHost.csproj', ['run'], [], true, { forceBuild: false });
        (aspireDebugSession as any)._resourceDebugSessions.push({
            id: 'resource-session',
            session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession,
            stopSession: sinon.stub().rejects(new Error('Resource stop failed')),
        });

        assert.strictEqual(restartHandler?.(aspireDebugSession.debugSessionId), true);
        await terminateSessionCallback?.(appHostDebugSession as unknown as vscode.DebugSession);

        assert.strictEqual(startDebuggingStub.callCount, 0, 'A replacement AppHost must not start after failed cleanup');
        sinon.assert.calledOnce(cliStop);
        sinon.assert.calledOnceWithExactly(terminateCliProcessTree, { force: true });
    });

    test('reports AppHost target version in end telemetry', async () => {
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
                distinctResourceTypes: ['project'],
                totalChildSessions: 1,
            }),
        };
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        let resolveTargetVersion: ((value: string) => void) | undefined;
        const targetVersionPromise = new Promise<string>(resolve => {
            resolveTargetVersion = resolve;
        });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').returns(targetVersionPromise);
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await waitFor(() => spawnStub.calledOnce);
            resolveTargetVersion!('13.6.0');
            await targetVersionPromise;
            const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            aspireDebugSession.dispose();
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const event = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.apphost_target_version, '13.6.0');
        }
        finally {
            resolveTargetVersion?.('13.6.0');
            restoreReporter();
        }
    });

    test('reports AppHost end duration before async metadata enrichment completes', async () => {
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        let resolveTargetVersion: ((value: string) => void) | undefined;
        const targetVersionPromise = new Promise<string>(resolve => {
            resolveTargetVersion = resolve;
        });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').returns(targetVersionPromise);
        sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await clock.tickAsync(100);
            aspireDebugSession.dispose();
            await clock.tickAsync(500);
            await clock.tickAsync(10_000);
            resolveTargetVersion!('13.6.0');
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const event = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_target_version, '13.6.0');
            assert.ok(event.measurements?.duration_ms !== undefined);
            assert.ok(event.measurements.duration_ms < 1_000, `Expected duration to exclude async metadata wait, got ${event.measurements.duration_ms}ms.`);
        }
        finally {
            resolveTargetVersion?.('13.6.0');
            restoreReporter();
        }
    });

    test('reports resolved AppHost directory classification in end telemetry', async () => {
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
                program: '/workspace/apphost',
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        let resolveLanguage: ((value: 'csharp' | 'typescript' | 'unknown') => void) | undefined;
        const languagePromise = new Promise<'csharp' | 'typescript' | 'unknown'>(resolve => {
            resolveLanguage = resolve;
        });
        sinon.stub(aspireDebugSession as any, 'isDirectory').resolves(true);
        sinon.stub(aspireDebugSession as any, 'resolveAppHostLanguageAtLaunch').returns(languagePromise);
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').resolves('unknown');
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await waitFor(() => spawnStub.calledOnce);
            const startEvent = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/start');
            assert.ok(startEvent);
            assert.strictEqual(Object.prototype.hasOwnProperty.call(startEvent.properties ?? {}, 'apphost_is_directory'), false);

            resolveLanguage!('typescript');
            await languagePromise;
            const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            aspireDebugSession.dispose();
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const endEvent = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(endEvent);
            assert.strictEqual(endEvent.properties?.apphost_language, 'typescript');
            assert.strictEqual(endEvent.properties?.apphost_is_directory, 'true');
        }
        finally {
            resolveLanguage?.('typescript');
            restoreReporter();
        }
    });

    test('Rust directory telemetry reports the AppHost language', async () => {
        const appHostDirectory = makeTempDir();
        writeFileSync(join(appHostDirectory, 'apphost.rs'), 'fn main() {}');
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
                program: appHostDirectory,
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        sinon.stub(aspireDebugSession as any, 'resolveAppHostTargetVersionAtLaunch').resolves('unknown');
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await waitFor(() => spawnStub.calledOnce);
            const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            aspireDebugSession.dispose();
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const event = fake.events.find(candidate => candidate.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(event);
            assert.strictEqual(event.properties?.apphost_language, 'rust');
        }
        finally {
            restoreReporter();
        }
    });

    test('uses workspace default candidate only for directory launch telemetry enrichment', async () => {
        const workspaceDir = makeTempDir();
        const appHostDir = join(workspaceDir, 'NestedAppHost');
        mkdirSync(appHostDir);
        const appHostPath = join(appHostDir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { createBuilder } from "./.aspire/modules/aspire";');
        writeFileSync(join(appHostDir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.6.0' } }));
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
                program: workspaceDir,
                command: 'run',
                [appHostTelemetryTargetPathConfigKey]: appHostPath,
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
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        try {
            aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });
            await waitFor(() => spawnStub.calledOnce);
            assert.deepStrictEqual(spawnStub.firstCall.args[0], [
                'run',
                '--start-debug-session',
                '--nologo',
            ]);
            assert.strictEqual(spawnStub.firstCall.args[1], workspaceDir);

            await waitFor(() => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/start'));
            const startEvent = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/start');
            assert.ok(startEvent);
            assert.strictEqual(startEvent.properties?.apphost_language, 'typescript');
            assert.strictEqual(Object.prototype.hasOwnProperty.call(startEvent.properties ?? {}, 'apphost_target_version'), false);

            const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            aspireDebugSession.dispose();
            await waitForWithFakeClock(clock, () => fake.events.some(event => event.name === 'aspire/vscode/debug/apphost/end'));

            const endEvent = fake.events.find(event => event.name === 'aspire/vscode/debug/apphost/end');
            assert.ok(endEvent);
            assert.strictEqual(endEvent.properties?.apphost_language, 'typescript');
            assert.strictEqual(endEvent.properties?.apphost_target_version, '13.6.0');
            assert.strictEqual(endEvent.properties?.apphost_is_directory, 'true');
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
            environment: [
                { name: 'SECRET_TOKEN', value: 'cpp-secret' },
            ],
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, false);

        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.strictEqual(loggableConfig.environment, '<redacted>');
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

    test('redacts sensitive debugger environments even when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'lldb',
            name: 'Rust',
            request: 'launch',
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environment: [
                { name: 'SECRET_TOKEN', value: 'cpp-secret' },
            ],
        } as AspireResourceExtendedDebugConfiguration;
        markDebugConfigurationEnvironmentSensitive(debugConfig);

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.strictEqual(loggableConfig.environment, '<redacted>');
        assert.ok(!JSON.stringify(loggableConfig).includes('secret'));
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

    test('tracks an already-started resource and reports its process without launching another debug session', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
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
            type: 'coreclr',
            name: 'Azure Functions',
            request: 'launch',
        } as AspireResourceExtendedDebugConfiguration;
        const stopSession = sinon.stub();
        const alreadyStartedSession = {
            id: 'run-1',
            processId: 4242,
            session: { id: 'run-1' } as vscode.DebugSession,
            stopSession,
            termination: new Promise<number>(() => { }),
        };
        const sendNotification = sinon.stub();
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            { sendNotification } as any,
            terminalProvider as any,
            () => { });

        const result = aspireDebugSession.trackAlreadyStartedResourceSession(debugConfig, alreadyStartedSession);

        assert.strictEqual(result, alreadyStartedSession);
        assert.strictEqual(startDebuggingStub.called, false);
        assert.deepStrictEqual(sendNotification.firstCall.args[0], {
            notification_type: 'processRestarted',
            session_id: 'run-1',
            dcp_id: 'debug-1',
            pid: 4242,
        });

        aspireDebugSession.dispose();
        assert.strictEqual(stopSession.calledOnce, true);
    });

    test('reports termination of an already-started resource to DCP', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
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
            type: 'coreclr',
            name: 'Azure Functions',
            request: 'launch',
        } as AspireResourceExtendedDebugConfiguration;
        let completeSession: (exitCode: number) => void;
        const termination = new Promise<number>(resolve => {
            completeSession = resolve;
        });
        const sendNotification = sinon.stub();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            { sendNotification } as any,
            terminalProvider as any,
            () => { });

        aspireDebugSession.trackAlreadyStartedResourceSession(debugConfig, {
            id: 'run-1',
            processId: 4242,
            session: { id: 'run-1' } as vscode.DebugSession,
            stopSession: sinon.stub(),
            termination,
        });
        completeSession!(17);
        await termination;
        await Promise.resolve();

        assert.deepStrictEqual(sendNotification.getCalls().map(call => call.args[0]), [
            {
                notification_type: 'processRestarted',
                session_id: 'run-1',
                dcp_id: 'debug-1',
                pid: 4242,
            },
            {
                notification_type: 'sessionTerminated',
                session_id: 'run-1',
                dcp_id: 'debug-1',
                exit_code: 17,
            },
        ]);

        aspireDebugSession.dispose();
    });

    test('stops an already-started resource handed off after the Aspire session was disposed', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
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
            type: 'coreclr',
            name: 'Azure Functions',
            request: 'launch',
        } as AspireResourceExtendedDebugConfiguration;
        const stopSession = sinon.stub();
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        aspireDebugSession.dispose();

        const result = aspireDebugSession.trackAlreadyStartedResourceSession(debugConfig, {
            id: 'run-1',
            processId: 4242,
            session: { id: 'run-1' } as vscode.DebugSession,
            stopSession,
            termination: new Promise<number>(() => { }),
        });

        assert.strictEqual(result, undefined);
        assert.strictEqual(stopSession.calledOnce, true);
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

    // The MAUI retry loop sleeps 5s between attempts and _disposed is only set at the very end of
    // the ordered shutdown, so a loop gated on _disposed would keep retrying - and keep starting
    // sessions the shutdown has already snapshotted past - long after stopDebugging() began.
    test('stops retrying a MAUI start once the shutdown has begun', async () => {
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
        // The AppHost stop is held open so the shutdown is demonstrably IN PROGRESS but not finished
        // while the retry decision is made: _stopping is set, _disposed is not. That is the only
        // window in which the two latches differ.
        let releaseAppHostStop: (() => void) | undefined;
        const appHostStopGate = new Promise<void>(resolve => { releaseAppHostStop = resolve; });
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(async () => { });
        // Every attempt reports "did not start", which is what drives the retry loop.
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: 'apphost-session',
            session: { id: 'apphost-session', name: 'MauiAppHost' } as unknown as vscode.DebugSession,
            stopSession: () => appHostStopGate,
        };

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await clock.tickAsync(1);

        const attemptsBeforeShutdown = startDebuggingStub.callCount;
        assert.strictEqual(attemptsBeforeShutdown, 1, 'The first attempt should already have run');

        const stopPromise = aspireDebugSession.stopDebugging();
        await clock.tickAsync(1);

        assert.strictEqual((aspireDebugSession as any)._disposed, false, 'The shutdown must still be in progress for this test to mean anything');

        // Advance well past both remaining retry delays. The loop must have stopped.
        await clock.tickAsync(60_000);

        assert.strictEqual(
            startDebuggingStub.callCount,
            attemptsBeforeShutdown,
            'A MAUI start must not keep retrying once the shutdown has begun');

        // The AppHost stop was never released, so the 60s tick above also carried the shutdown past
        // its budget. It has to have given up rather than still be waiting - that is the whole point
        // of the bound - and the timeout has to be reported rather than swallowed.
        await assert.rejects(stopPromise, (err: Error) => {
            // The accepted MAUI start owns the shutdown until its 5-second retry delay observes the
            // shutdown latch. The AppHost then receives the roughly 5 seconds left in the shared
            // budget rather than incorrectly reporting that it waited for all 10 seconds.
            assert.strictEqual(err.message, debugSessionStopTimedOut('MauiAppHost', 6));
            return true;
        });

        releaseAppHostStop!();
        startSessionCallback = undefined;
        await clock.tickAsync(1);
        await sessionPromise;
        clock.restore();
    });

    test('resource stopSession deduplicates concurrent stops and retries after rejection', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        const firstStop = createDeferred<void>();
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost/AppHost.csproj',
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
            type: 'coreclr',
            name: 'Project',
            request: 'launch',
            program: '/workspace/App/bin/App.dll',
        } as AspireResourceExtendedDebugConfiguration;
        const resourceSession = {
            id: 'resource-session',
            type: 'coreclr',
            name: 'Project',
            configuration: debugConfig,
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async () => {
            startSessionCallback?.(resourceSession);
            return true;
        });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(firstStop.promise);
        stopDebugging.onSecondCall().resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });
        sinon.stub(aspireDebugSession as any, 'createDebugAdapterTrackerCore');

        const resource = await aspireDebugSession.startAndGetDebugSession(debugConfig);
        assert.ok(resource);

        const first = resource.stopSession();
        const concurrent = resource.stopSession();
        assert.strictEqual(concurrent, first);
        assert.strictEqual(stopDebugging.calledOnce, true);

        firstStop.reject(new Error('stop failed'));
        await assert.rejects(Promise.resolve(first), /stop failed/);

        await resource.stopSession();
        assert.strictEqual(stopDebugging.callCount, 2);

        aspireDebugSession.dispose();
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

    function createDeferred<T>(): {
        promise: Promise<T>;
        reject(reason?: unknown): void;
        resolve(value: T | PromiseLike<T>): void;
    } {
        let resolve!: (value: T | PromiseLike<T>) => void;
        let reject!: (reason?: unknown) => void;
        const promise = new Promise<T>((promiseResolve, promiseReject) => {
            resolve = promiseResolve;
            reject = promiseReject;
        });
        return { promise, reject, resolve };
    }

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

    function createSessionForSpawn(
        getAspireCliExecutablePath: () => Promise<string> = async () => '/usr/local/bin/aspire',
        removeAspireDebugSession: (session: AspireDebugSession) => void = () => { },
        onNewConnection: (callback: (client: any) => void) => vscode.Disposable = () => ({ dispose: () => { } })): AspireDebugSession {
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {},
        } as unknown as vscode.DebugSession;

        return new AspireDebugSession(
            parentDebugSession,
            { onNewConnection } as any,
            { recordAppHostProcessExit: () => { } } as any,
            {
                getAspireCliExecutablePath,
                createEnvironment: () => ({}),
            } as any,
            removeAspireDebugSession);
    }

    function createFakeCliProcess(pid: number, exitCode: number | null = null): ChildProcessWithoutNullStreams & { kill: sinon.SinonStub } {
        const kill = sinon.stub().returns(true);
        return Object.assign(new EventEmitter(), {
            stdin: new PassThrough(),
            stdout: new PassThrough(),
            stderr: new PassThrough(),
            killed: false,
            exitCode,
            signalCode: null,
            pid,
            kill,
        }) as unknown as ChildProcessWithoutNullStreams & { kill: sinon.SinonStub };
    }
});
