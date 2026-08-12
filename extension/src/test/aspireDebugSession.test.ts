import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { join } from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import { AspireDebugSession, buildAspireCommandArgs, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { AspireResourceExtendedDebugConfiguration } from '../dcp/types';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

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
            // callback has to release the same ownership because the later dispose() call is a no-op.
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

    test('stopDebugging stops the AppHost debug session before the Aspire parent session', async () => {
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

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
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
