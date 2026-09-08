import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import childProcess = require('node:child_process');
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { delimiter as pathDelimiter, dirname, join } from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWorkspaceFolder, fsPathOf, removeDirectorySafely } from './testHelpers';
import * as cliModule from '../utils/process/cliProcess';
import * as cliPathModule from '../utils/cliPath';
import * as debuggerExtensionsModule from '../debugger/debuggerExtensions';
import { AspireDebugSession, buildAspireCommandArgs, getLoggableDebugConfiguration, markDebugConfigurationEnvironmentSensitive } from '../debugger/AspireDebugSession';
import { AspireDebugConfigurationProvider } from '../debugger/AspireDebugConfigurationProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { isAspireDebugConfigurationExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { AspireExtendedDebugConfiguration, AspireResourceExtendedDebugConfiguration, JavaLaunchConfiguration, NodeLaunchConfiguration, ProjectLaunchConfiguration, RustLaunchConfiguration } from '../dcp/types';
import {
    __resetLaunchFailureJournalForTests,
    readLatestLaunchFailures,
    type LaunchFailureRecord,
} from '../services/launchFailureJournal';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';
import { aspireDashboard, debugSessionStopTimedOut } from '../loc/strings';
import { registerRunCleanup } from '../debugger/runCleanupRegistry';
import { __resetAppHostIdentityRegistryForTests } from '../utils/appHostIdentity';
import { AppHostBuildFailureError } from '../debugger/appHostBuildFailureError';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';

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

    function trackDashboardDebugSession(
        aspireDebugSession: AspireDebugSession,
        dashboardDebugSession: vscode.DebugSession): void {
        (aspireDebugSession as any)._dashboardLauncher.trackDashboardDebugSession(dashboardDebugSession);
    }

    function getTrackedDashboardDebugSessions(
        aspireDebugSession: AspireDebugSession): readonly vscode.DebugSession[] {
        return [...(aspireDebugSession as any)._dashboardLauncher._dashboardDebugSessions.values()]
            .map((tracked: { session: vscode.DebugSession }) => tracked.session);
    }

    setup(() => {
        __resetAppHostIdentityRegistryForTests();
        __resetLaunchFailureJournalForTests();
    });

    teardown(() => {
        __resetLaunchFailureJournalForTests();
        __resetAppHostIdentityRegistryForTests();
        sinon.restore();
        __resetCommonPropertiesForTests();
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                removeDirectorySafely(dir);
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

    test('records a sanitized CLI spawn failure for the owning AppHost', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        const cliProcess = createFakeCliProcess(4330);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const aspireDebugSession = createSessionForSpawn();
        const requestOrderedShutdown = sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        aspireDebugSession.configuration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: appHostPath,
            command: 'run',
            noDebug: false,
        };
        const rawError = Object.assign(new Error('raw-spawn-message'), {
            code: 'ENOENT',
            path: '/private/raw-cli-path',
            token: 'raw-cli-token',
        });

        await aspireDebugSession.spawnAspireCommand(['run'], dirname(appHostPath), false, 'aspire run');
        spawnStub.firstCall.args[3]?.errorCallback?.(rawError);

        const records = readLatestLaunchFailures(appHostPath);
        assert.strictEqual(records.length, 1);
        sinon.assert.calledOnce(requestOrderedShutdown);
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'cliLaunch',
            category: 'missingDependency',
            controller: 'cli',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(readLatestLaunchFailures(appHostPath));
        assert.strictEqual(serialized.includes(rawError.message), false);
        assert.strictEqual(serialized.includes(rawError.path), false);
        assert.strictEqual(serialized.includes(rawError.token), false);
    });

    test('records a sanitized CLI executable resolution failure for the owning AppHost', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        const rawError = Object.assign(new Error('raw-resolution-message'), {
            code: 'ENOENT',
            path: '/private/raw-cli-path',
            environment: { RAW_SECRET: 'raw-environment-secret' },
        });
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const aspireDebugSession = createSessionForSpawn(async () => {
            throw rawError;
        });
        const requestOrderedShutdown = sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        aspireDebugSession.configuration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: appHostPath,
            command: 'run',
            noDebug: false,
        };

        await aspireDebugSession.spawnAspireCommand(['run'], dirname(appHostPath), false, 'aspire run');

        sinon.assert.notCalled(spawnStub);
        sinon.assert.calledOnce(requestOrderedShutdown);
        assert.strictEqual(readLatestLaunchFailures(appHostPath).length, 1);
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'cliLaunch',
            category: 'missingDependency',
            controller: 'cli',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(readLatestLaunchFailures(appHostPath));
        assert.strictEqual(serialized.includes(rawError.message), false);
        assert.strictEqual(serialized.includes(rawError.path), false);
        assert.strictEqual(serialized.includes(rawError.environment.RAW_SECRET), false);
    });

    test('records a sanitized synchronous CLI spawn failure for the owning AppHost', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        const rawError = Object.assign(new Error('raw-synchronous-spawn-message'), {
            code: 'EACCES',
            path: '/private/raw-cli-path',
            arguments: ['--raw-secret'],
        });
        sinon.stub(cliModule, 'spawnCliProcess').throws(rawError);
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const aspireDebugSession = createSessionForSpawn();
        const requestOrderedShutdown = sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        aspireDebugSession.configuration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: appHostPath,
            command: 'run',
            noDebug: true,
        };

        await aspireDebugSession.spawnAspireCommand(['run'], dirname(appHostPath), true, 'aspire run');

        sinon.assert.calledOnce(requestOrderedShutdown);
        assert.strictEqual(readLatestLaunchFailures(appHostPath).length, 1);
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'cliLaunch',
            category: 'permissionDenied',
            controller: 'cli',
            mode: 'run',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(readLatestLaunchFailures(appHostPath));
        assert.strictEqual(serialized.includes(rawError.message), false);
        assert.strictEqual(serialized.includes(rawError.path), false);
        assert.strictEqual(serialized.includes(rawError.arguments[0]), false);
    });

    test('records a nonzero CLI exit before startup and ignores shutdown exits', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        const cliProcess = createFakeCliProcess(4331, 1);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        sinon.stub(cliModule, 'terminateCliProcess');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();
        aspireDebugSession.configuration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: appHostPath,
            command: 'run',
            noDebug: true,
        };

        await aspireDebugSession.spawnAspireCommand(['run'], dirname(appHostPath), true, 'aspire run');
        spawnStub.firstCall.args[3]?.exitCallback?.(1);

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'cliLaunch',
            category: 'processExited',
            controller: 'cli',
            mode: 'run',
            providerKind: 'dotnet',
            exitCodeBucket: 'one',
        });

        __resetLaunchFailureJournalForTests();
        (aspireDebugSession as any)._stopping = true;
        spawnStub.firstCall.args[3]?.exitCallback?.(1);
        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    function createSessionWithConfiguration(
        configuration: Record<string, unknown>,
        getAspireCliExecutablePath: sinon.SinonStub,
    ): AspireDebugSession {
        const parentDebugSession = {
            id: 'aspire-session',
            configuration,
        } as unknown as vscode.DebugSession;

        return new AspireDebugSession(
            parentDebugSession,
            { onNewConnection: () => ({ dispose: () => { } }) } as any,
            { recordAppHostProcessExit: () => { } } as any,
            { getAspireCliExecutablePath, createEnvironment: () => ({}) } as any,
            () => { });
    }

    test('spawnAspireCommand resolves the CLI using the target derived from the AppHost path', async () => {
        sinon.stub(cliModule, 'spawnCliProcess').returns(createFakeCliProcess(4330));
        const folder = createWorkspaceFolder('workspace', '/workspace');
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
            uri.fsPath === fsPathOf('/workspace/AppHost.csproj') ? folder : undefined);
        const getAspireCliExecutablePath = sinon.stub().resolves('/workspace/aspire');
        const aspireDebugSession = createSessionWithConfiguration({ program: '/workspace/AppHost.csproj' }, getAspireCliExecutablePath);

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        assert.ok(getAspireCliExecutablePath.calledOnceWith(workspaceFolderCliPathTarget(folder)));
    });

    test('spawnAspireCommand uses the CLI path verified by the launch service', async () => {
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(createFakeCliProcess(4334));
        const getAspireCliExecutablePath = sinon.stub().resolves('/second/aspire');
        const aspireDebugSession = createSessionWithConfiguration({
            program: '/workspace/AppHost.csproj',
            resolvedCliPath: '/verified/aspire',
        }, getAspireCliExecutablePath);

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        assert.strictEqual(getAspireCliExecutablePath.called, false);
        assert.strictEqual(spawnStub.firstCall.args[1], '/verified/aspire');
    });

    test('spawnAspireCommand prefers the resolved AppHost path over the configured program', async () => {
        sinon.stub(cliModule, 'spawnCliProcess').returns(createFakeCliProcess(4331));
        const folder = createWorkspaceFolder('other', '/other');
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
            uri.fsPath === fsPathOf('/other/Program.cs') ? folder : undefined);
        const getAspireCliExecutablePath = sinon.stub().resolves('/other/aspire');
        const aspireDebugSession = createSessionWithConfiguration({
            program: '/workspace',
            [appHostTelemetryTargetPathConfigKey]: '/other/Program.cs',
        }, getAspireCliExecutablePath);

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        assert.ok(getAspireCliExecutablePath.calledOnceWith(workspaceFolderCliPathTarget(folder)));
    });

    test('spawnAspireCommand falls back to the working directory target when no AppHost path is configured', async () => {
        sinon.stub(cliModule, 'spawnCliProcess').returns(createFakeCliProcess(4332));
        const folder = createWorkspaceFolder('workspace', '/workspace');
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
            uri.fsPath === fsPathOf('/workspace') ? folder : undefined);
        const getAspireCliExecutablePath = sinon.stub().resolves('/workspace/aspire');
        const aspireDebugSession = createSessionWithConfiguration({}, getAspireCliExecutablePath);

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        assert.ok(getAspireCliExecutablePath.calledOnceWith(workspaceFolderCliPathTarget(folder)));
    });

    test('spawnAspireCommand uses the window target when no AppHost path or working directory is available', async () => {
        sinon.stub(cliModule, 'spawnCliProcess').returns(createFakeCliProcess(4333));
        const getAspireCliExecutablePath = sinon.stub().resolves('aspire');
        const aspireDebugSession = createSessionWithConfiguration({}, getAspireCliExecutablePath);

        await aspireDebugSession.spawnAspireCommand(['run'], undefined, false, 'aspire run');

        assert.ok(getAspireCliExecutablePath.calledOnceWith(windowCliPathTarget));
    });

    test('redacts forwarded AppHost arguments from shutdown logs', async () => {
        const cliProcess = createFakeCliProcess(4323);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(cliProcess);
        sinon.stub(cliModule, 'terminateCliProcess').resolves();
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const logStub = sinon.stub(extensionLogOutputChannel, 'info');
        const aspireDebugSession = createSessionForSpawn();

        await aspireDebugSession.spawnAspireCommand(
            ['run', '--isolated', '--', '--api-key', 'secret-value'],
            '/workspace',
            false,
            'aspire run');

        aspireDebugSession.dispose();
        await aspireDebugSession.stopDebugging();

        const shutdownMessage = logStub.args
            .map(([message]) => message)
            .find(message => message.startsWith('Requested Aspire CLI exit with args:'));
        assert.strictEqual(
            shutdownMessage,
            'Requested Aspire CLI exit with args: run --isolated -- <redacted>');

        spawnStub.firstCall.args[3]?.exitCallback?.(0);
    });

    test('logs only the forwarded AppHost argument count when starting the debugger', async () => {
        const logStub = sinon.stub(extensionLogOutputChannel, 'info');
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--no-build', '--', '--api-key', 'secret-value'],
            [],
            true,
            { forceBuild: false });

        const startMessage = logStub.args
            .map(([message]) => message)
            .find(message => message.startsWith('Starting AppHost for project:'));
        assert.strictEqual(
            startMessage,
            'Starting AppHost for project: /workspace/AppHost.csproj with argument count: 2');
    });

    test('does not forward CLI options to the AppHost debugger without a separator', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--isolated'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(createDebugSessionConfiguration.firstCall.args[2], undefined);
    });

    test('forwards the typed launch profile to the AppHost project debugger', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession.configuration as AspireExtendedDebugConfiguration).launchProfile = 'Development HTTPS';
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--launch-profile=Development HTTPS'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(
            (createDebugSessionConfiguration.firstCall.args[1] as ProjectLaunchConfiguration).launch_profile,
            'Development HTTPS');
        assert.strictEqual(createDebugSessionConfiguration.firstCall.args[2], undefined);
    });

    test('forwards the CLI launch profile to the AppHost project debugger', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--launch-profile=Terminal Profile', '--', '--app-argument'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(
            (createDebugSessionConfiguration.firstCall.args[1] as ProjectLaunchConfiguration).launch_profile,
            'Terminal Profile');
        assert.deepStrictEqual(createDebugSessionConfiguration.firstCall.args[2], ['--app-argument']);
    });

    test('nested AppHost settings override the CLI launch profile at the AppHost debugger boundary', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession.configuration as AspireExtendedDebugConfiguration).debuggers = {
            apphost: {
                launchProfile: 'AppHost Override',
            },
        };
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--launch-profile=Terminal Profile'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(
            (createDebugSessionConfiguration.firstCall.args[1] as ProjectLaunchConfiguration).launch_profile,
            'AppHost Override');
    });

    test('nested AppHost settings can disable a CLI launch profile at the AppHost debugger boundary', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession.configuration as AspireExtendedDebugConfiguration).launchProfile = 'Top Level';
        (aspireDebugSession.configuration as AspireExtendedDebugConfiguration).debuggers = {
            apphost: {
                disableLaunchProfile: true,
            },
        };
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/AppHost.csproj',
            ['run', '--launch-profile=Terminal Profile'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(
            (createDebugSessionConfiguration.firstCall.args[1] as ProjectLaunchConfiguration).launch_profile,
            undefined);
    });

    test('does not apply project debugger launch profiles to non-dotnet AppHosts', async () => {
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'node',
            request: 'launch',
            name: 'AppHost',
            runId: '',
            debugSessionId: 'aspire-session',
        } as AspireResourceExtendedDebugConfiguration);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession.configuration as AspireExtendedDebugConfiguration).debuggers = {
            project: {
                launchProfile: 'Project Resource Profile',
            },
        };
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/apphost.ts',
            ['run', '--launch-profile=Terminal Profile'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(
            (createDebugSessionConfiguration.firstCall.args[1] as ProjectLaunchConfiguration).launch_profile,
            undefined);
    });

    test('terminateCliProcessTree signals a running CLI process and still collects an exited one', async () => {
        // `terminateCliProcess` is stubbed rather than executed: on Windows it shells out to
        // `taskkill /pid <pid> /t` instead of calling `child.kill`, so running it for real would
        // both fail this assertion on the Windows CI agents and signal whatever process happens to
        // own the made-up PID there.
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const running = createFakeCliProcess(4322);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession as any)._cliProcess = running;

        await aspireDebugSession.terminateCliProcessTree();

        // The cooperative `stopCli` RPC cannot terminate the process, so the signal is what
        // actually ends the CLI and the resource tree beneath it.
        sinon.assert.calledOnce(terminateStub);
        assert.strictEqual(terminateStub.firstCall.args[0], running);

        const exited = createFakeCliProcess(4323, 0);
        const exitedAspireDebugSession = createSessionForSpawn();
        (exitedAspireDebugSession as any)._cliProcess = exited;

        await exitedAspireDebugSession.terminateCliProcessTree();

        // An exited leader is still forwarded: `terminateCliProcess` reaps the surviving members of
        // its managed process group, which is the only path that collects an AppHost and resource
        // processes that outlived the CLI.
        sinon.assert.calledTwice(terminateStub);
        assert.strictEqual(terminateStub.secondCall.args[0], exited);
    });

    test('terminateCliProcessTree is idempotent after signalling a CLI process', async () => {
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const cliProcess = createFakeCliProcess(4324);
        const aspireDebugSession = createSessionForSpawn();
        (aspireDebugSession as any)._cliProcess = cliProcess;

        await Promise.all([
            aspireDebugSession.terminateCliProcessTree({ force: true }),
            aspireDebugSession.terminateCliProcessTree(),
        ]);

        sinon.assert.calledOnce(terminateStub);
        assert.strictEqual(terminateStub.firstCall.args[0], cliProcess);
        assert.deepStrictEqual(terminateStub.firstCall.args[2], { force: true });
    });

    test('a disposed session remains owned until CLI process-tree termination settles', async () => {
        const termination = createDeferred<void>();
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').returns(termination.promise);
        const removeAspireDebugSession = sinon.stub();
        const aspireDebugSession = createSessionForSpawn(
            async () => '/usr/local/bin/aspire',
            removeAspireDebugSession);
        (aspireDebugSession as any)._cliProcess = createFakeCliProcess(4330);

        const terminating = aspireDebugSession.terminateCliProcessTree({ force: true });
        aspireDebugSession.finalizeForExtensionShutdown();

        sinon.assert.notCalled(removeAspireDebugSession);

        termination.resolve();
        await terminating;

        sinon.assert.calledOnceWithExactly(removeAspireDebugSession, aspireDebugSession);
        sinon.assert.calledOnce(terminateStub);
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
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
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
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
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
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
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
        sinon.stub(cliModule, 'terminateCliProcess').resolves();
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
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = createSessionForSpawn();

        await aspireDebugSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        await aspireDebugSession.terminateCliProcessTree({ force: true });
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

    suite('launch command arguments', () => {
        test('omits the debug session flag for a no-debug do launch', async () => {
            const args = await captureLaunchCommandArgs('do', true);

            assert.deepStrictEqual(args, [
                'do',
                'build',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });

        test('includes the debug session flag for a debug do launch', async () => {
            const args = await captureLaunchCommandArgs('do', false);

            assert.deepStrictEqual(args, [
                'do',
                'build',
                '--start-debug-session',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });

        test('preserves no-debug run launch arguments', async () => {
            const args = await captureLaunchCommandArgs('run', true);

            assert.deepStrictEqual(args, [
                'run',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });

        test('preserves debug run launch arguments', async () => {
            const args = await captureLaunchCommandArgs('run', false);

            assert.deepStrictEqual(args, [
                'run',
                '--start-debug-session',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });

        test('ignores noDebug for deploy launch arguments', async () => {
            const args = await captureLaunchCommandArgs('deploy', true);

            assert.deepStrictEqual(args, [
                'deploy',
                '--start-debug-session',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });

        test('ignores noDebug for publish launch arguments', async () => {
            const args = await captureLaunchCommandArgs('publish', true);

            assert.deepStrictEqual(args, [
                'publish',
                '--start-debug-session',
                '--nologo',
                '--apphost',
                '/workspace/apphost.cs',
            ]);
        });
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

    test('suppresses the Aspire CLI first-run banner when AppHost arguments include nologo', async () => {
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
                args: ['--', '--nologo'],
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
        assert.deepStrictEqual(spawnStub.firstCall.args[0], [
            'run',
            '--start-debug-session',
            '--nologo',
            '--apphost',
            '/workspace/apphost.cs',
            '--',
            '--nologo',
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

    test('an explicit CLI directory launch passes apphost and selection origin to the Aspire CLI', async () => {
        const appHostDirectory = makeTempDir();
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
                __aspireAppHostSelectionOrigin: 'explicit-cli',
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
        assert.deepStrictEqual(spawnStub.firstCall.args[0], [
            'run',
            '--start-debug-session',
            '--nologo',
            '--apphost',
            appHostDirectory,
        ]);
        assert.strictEqual(spawnStub.firstCall.args[1], appHostDirectory);
        assert.deepStrictEqual(spawnStub.firstCall.args[4], [{
            name: 'ASPIRE_CLI_APPHOST_SELECTION_ORIGIN',
            value: 'explicit-cli',
        }]);
    });

    test('a default-discovery directory launch omits the apphost argument', async () => {
        const appHostDirectory = makeTempDir();
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
                __aspireAppHostSelectionOrigin: 'default-discovery',
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
        assert.deepStrictEqual(spawnStub.firstCall.args[0], [
            'run',
            '--start-debug-session',
            '--nologo',
        ]);
        assert.strictEqual(spawnStub.firstCall.args[1], appHostDirectory);
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
        trackDashboardDebugSession(aspireDebugSession, dashboardDebugSession);

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
        trackDashboardDebugSession(aspireDebugSession, dashboardDebugSession);

        aspireDebugSession.dispose();
        await aspireDebugSession.stopDebugging();

        sinon.assert.calledOnceWithExactly(stopDebugging, parentDebugSession);
        assert.deepStrictEqual(getTrackedDashboardDebugSessions(aspireDebugSession), []);
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
        trackDashboardDebugSession(aspireDebugSession, dashboardDebugSession);

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /Dashboard stop failed/);
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebugging.callCount, 3);
        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);
        assert.strictEqual(stopDebugging.thirdCall.args[0], dashboardDebugSession);
    });

    test('openDashboard does not wait for a dashboard debug session to start', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(() => { }));
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', false);

        sinon.assert.calledOnce(startDebugging);
    });

    test('canceling an explicit dashboard handoff stops a browser that starts late', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        let resolveStartDebugging!: (didStart: boolean) => void;
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging').returns(
            new Promise<boolean>(resolve => {
                resolveStartDebugging = resolve;
            }));
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const cancellation = new vscode.CancellationTokenSource();

        const openPromise = aspireDebugSession.openDashboard(
            'https://localhost:1234',
            'debugEdge',
            true,
            cancellation.token);
        await Promise.resolve();
        cancellation.cancel();

        await assert.rejects(openPromise, error => error instanceof vscode.CancellationError);

        const dashboardDebugSession = {
            id: 'dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: startDebugging.firstCall.args[1],
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        startSessionCallback?.(dashboardDebugSession);
        resolveStartDebugging(true);
        await new Promise(resolve => setImmediate(resolve));

        sinon.assert.calledOnceWithExactly(stopDebugging, dashboardDebugSession);
    });

    test('a pending dashboard debug launch disposes its start listener after shutdown', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const disposeStartListener = sinon.stub();
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: disposeStartListener });
        sinon.stub(vscode.debug, 'startDebugging').returns(new Promise<boolean>(() => { }));
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', false);
        const stopPromise = aspireDebugSession.stopDebugging();
        await clock.tickAsync(2000);
        await stopPromise;
        sinon.assert.notCalled(disposeStartListener);

        await clock.tickAsync(29999);
        sinon.assert.notCalled(disposeStartListener);

        await clock.tickAsync(1);
        sinon.assert.calledOnce(disposeStartListener);
    });

    test('a delayed dashboard stop preserves the reserved AppHost stop budget', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const delayedShutdownWork: sinon.SinonStub[] = [];
        const addDelayedShutdownWork = (phase: string, pendingStartDelayMs: number, lateResourceStopDelayMs: number) => {
            const pendingStart = aspireDebugSession.beginPendingDebugSessionStart(`${phase} pending start`);
            setTimeout(() => pendingStart.dispose(), pendingStartDelayMs);

            const stopLateResource = sinon.stub().callsFake(
                () => new Promise<void>(resolve => setTimeout(resolve, lateResourceStopDelayMs)));
            delayedShutdownWork.push(stopLateResource);
            const tracked = aspireDebugSession.trackAlreadyStartedResourceSession(
                { type: 'node', request: 'launch', name: phase, runId: phase, debugSessionId: null } as any,
                {
                    id: `${phase}-resource`,
                    processId: 1234,
                    session: { id: `${phase}-resource`, name: `${phase} resource` } as unknown as vscode.DebugSession,
                    stopSession: stopLateResource,
                    termination: new Promise<number>(() => { }),
                });
            assert.strictEqual(tracked, undefined);
        };
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(session => {
            assert.strictEqual(session, parentDebugSession);
            addDelayedShutdownWork('parent', 5, 7);
            return new Promise<void>(resolve => setTimeout(resolve, 3));
        });
        const stopAppHost = sinon.stub().callsFake(() => {
            addDelayedShutdownWork('AppHost', 3, 5);
            return new Promise<void>(resolve => setTimeout(resolve, 1));
        });
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession,
            stopSession: stopAppHost,
        };
        sinon.stub(
            (aspireDebugSession as any)._dashboardLauncher,
            'stopDashboardWithinBudget').callsFake(async () => {
                clock.setSystemTime(clock.now + 10000);
            });

        const stopPromise = aspireDebugSession.stopDebugging();
        await Promise.resolve();
        for (let elapsedMs = 0; elapsedMs < 12; elapsedMs++) {
            await clock.tickAsync(1);
        }
        await stopPromise;

        sinon.assert.calledOnce(stopAppHost);
        sinon.assert.calledOnceWithExactly(stopDebugging, parentDebugSession);
        delayedShutdownWork.forEach(stopLateResource => sinon.assert.calledOnce(stopLateResource));
    });

    test('a rejected dashboard debug launch disposes its start listener and logs the failure', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const disposeStartListener = sinon.stub();
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: disposeStartListener });
        sinon.stub(vscode.debug, 'startDebugging').rejects(new Error('Browser launch failed'));
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', false);
        await new Promise(resolve => setImmediate(resolve));

        sinon.assert.calledOnce(disposeStartListener);
        sinon.assert.calledOnceWithExactly(
            warn,
            'Failed to launch dashboard debug session (pwa-msedge).');
    });

    test('a synchronous dashboard debug launch failure disposes its start listener and logs the failure', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const disposeStartListener = sinon.stub();
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: disposeStartListener });
        sinon.stub(vscode.debug, 'startDebugging').throws(new Error('Browser launch failed'));
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', false);
        await new Promise(resolve => setImmediate(resolve));

        sinon.assert.calledOnce(disposeStartListener);
        sinon.assert.calledOnceWithExactly(
            warn,
            'Failed to launch dashboard debug session (pwa-msedge).');
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

    test('stopDebugging stops every explicitly opened dashboard debug session', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const firstDashboardDebugSession = {
            id: 'first-dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        const secondDashboardDebugSession = {
            id: 'second-dashboard-session',
            type: 'pwa-chrome',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const startListeners: Array<{
            callback: (session: vscode.DebugSession) => void;
            disposed: boolean;
        }> = [];
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            const listener = { callback, disposed: false };
            startListeners.push(listener);
            return { dispose: () => { listener.disposed = true; } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const firstOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        startListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(firstDashboardDebugSession));
        await firstOpen;
        const secondOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugChrome');
        startListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(secondDashboardDebugSession));
        await secondOpen;

        await aspireDebugSession.stopDebugging();

        assert.deepStrictEqual(
            stopDebugging.getCalls().map(call => call.args[0]),
            [firstDashboardDebugSession, secondDashboardDebugSession, parentDebugSession]);
        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher.hasSessionsToStop, false);
    });

    test('overlapping same-browser dashboard launches keep start-event ownership isolated', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const startListeners: Array<{
            callback: (session: vscode.DebugSession) => void;
            disposed: boolean;
        }> = [];
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            const listener = { callback, disposed: false };
            startListeners.push(listener);
            return { dispose: () => { listener.disposed = true; } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: sinon.stub() });
        let resolveFirstStart!: (didStart: boolean) => void;
        let resolveSecondStart!: (didStart: boolean) => void;
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging');
        startDebugging.onFirstCall().returns(new Promise<boolean>(resolve => {
            resolveFirstStart = resolve;
        }));
        startDebugging.onSecondCall().returns(new Promise<boolean>(resolve => {
            resolveSecondStart = resolve;
        }));
        const openExternal = sinon.stub(vscode.env, 'openExternal').resolves(true);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const firstOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', true);
        const secondOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge', true);
        const secondDebugConfiguration = startDebugging.secondCall.args[1] as vscode.DebugConfiguration;
        const secondDashboardDebugSession = {
            id: 'second-dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: secondDebugConfiguration,
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;

        startListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(secondDashboardDebugSession));
        resolveFirstStart(false);
        resolveSecondStart(true);

        assert.deepStrictEqual(await Promise.all([firstOpen, secondOpen]), ['externalBrowser', 'debugBrowser']);
        assert.strictEqual(openExternal.callCount, 1);
        assert.strictEqual(startListeners.every(listener => listener.disposed), true);
        assert.deepStrictEqual(getTrackedDashboardDebugSessions(aspireDebugSession), [secondDashboardDebugSession]);

        await aspireDebugSession.stopDebugging();
        assert.deepStrictEqual(
            stopDebugging.getCalls().map(call => call.args[0]),
            [secondDashboardDebugSession, parentDebugSession]);
    });

    test('natural dashboard termination keeps other opened dashboard sessions tracked', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {},
        } as unknown as vscode.DebugSession;
        const firstDashboardDebugSession = {
            id: 'first-dashboard-session',
            type: 'pwa-msedge',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        const secondDashboardDebugSession = {
            id: 'second-dashboard-session',
            type: 'pwa-chrome',
            name: aspireDashboard,
            configuration: { name: aspireDashboard },
            parentSession: parentDebugSession,
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({
            get: <T>(section: string, defaultValue: T) =>
                section === 'closeDashboardOnDebugEnd' ? true as T : defaultValue,
        } as vscode.WorkspaceConfiguration);
        const startListeners: Array<{
            callback: (session: vscode.DebugSession) => void;
            disposed: boolean;
        }> = [];
        const terminationListeners: Array<{
            callback: (session: vscode.DebugSession) => void;
            disposed: boolean;
        }> = [];
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            const listener = { callback, disposed: false };
            startListeners.push(listener);
            return { dispose: () => { listener.disposed = true; } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            const listener = { callback, disposed: false };
            terminationListeners.push(listener);
            return { dispose: () => { listener.disposed = true; } };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const firstOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        startListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(firstDashboardDebugSession));
        await firstOpen;
        const secondOpen = aspireDebugSession.openDashboard('https://localhost:1234', 'debugChrome');
        startListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(secondDashboardDebugSession));
        await secondOpen;

        terminationListeners.filter(listener => !listener.disposed)
            .forEach(listener => listener.callback(secondDashboardDebugSession));

        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher.hasSessionsToStop, true);
        await aspireDebugSession.stopDebugging();
        assert.deepStrictEqual(
            stopDebugging.getCalls().map(call => call.args[0]),
            [firstDashboardDebugSession, parentDebugSession]);
        assert.strictEqual((aspireDebugSession as any)._dashboardLauncher.hasSessionsToStop, false);
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
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        let dashboardStopAttempts = 0;
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === dashboardDebugSession && dashboardStopAttempts++ === 0) {
                throw new Error('Late dashboard stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        const firstStop = aspireDebugSession.stopDebugging();
        startSessionCallback?.(dashboardDebugSession);
        resolveStartDebugging?.(true);
        await startDebuggingPromise;

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
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        const stopPromise = aspireDebugSession.stopDebugging();
        startSessionCallback?.(dashboardDebugSession);
        await clock.tickAsync(2000);
        await stopPromise;

        assert.strictEqual(stopDebugging.firstCall.args[0], dashboardDebugSession);
        assert.strictEqual(stopDebugging.secondCall.args[0], parentDebugSession);

        resolveStartDebugging?.(true);
        await startDebuggingPromise;
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
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        let dashboardStopAttempts = 0;
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === dashboardDebugSession && dashboardStopAttempts++ === 0) {
                throw new Error('Late dashboard stop failed');
            }
        });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
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
        await startDebuggingPromise;
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
        trackDashboardDebugSession(aspireDebugSession, dashboardDebugSession);

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
        const trackedAfterForeignSession = getTrackedDashboardDebugSessions(aspireDebugSession);
        startSessionCallback?.(ownDashboardSession);
        await openPromise;

        assert.deepStrictEqual(trackedAfterForeignSession, []);
        assert.deepStrictEqual(getTrackedDashboardDebugSessions(aspireDebugSession), [ownDashboardSession]);
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
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        aspireDebugSession.dispose();
        startSessionCallback?.(dashboardDebugSession);
        resolveStartDebugging?.(true);
        await startDebuggingPromise;
        await aspireDebugSession.stopDebugging();

        assert.strictEqual(stopDebugging.calledWith(dashboardDebugSession), true);
        assert.deepStrictEqual(getTrackedDashboardDebugSessions(aspireDebugSession), []);
    });

    test('launching the dashboard browser does not fall back to an external browser after the Aspire session was disposed', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(() => ({ dispose: sinon.stub() }));
        let resolveStartDebugging: ((didStart: boolean) => void) | undefined;
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStartDebugging = resolve;
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const openExternal = sinon.stub(vscode.env, 'openExternal').resolves(true);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234', 'debugEdge');
        aspireDebugSession.dispose();
        resolveStartDebugging?.(false);
        await startDebuggingPromise;

        assert.strictEqual(openExternal.called, false);
        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('a rejected shutdown blocks later dashboard browser presentations', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const resourceStop = sinon.stub().rejects(new Error('Resource stop failed'));
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const onDidStartDebugSession = sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({
            dispose: sinon.stub(),
        });
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves();
        const openExternal = sinon.stub(vscode.env, 'openExternal').resolves(true);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        (aspireDebugSession as any)._resourceDebugSessions = [{
            id: 'resource-session',
            session: { id: 'resource-session', name: 'Resource' } as unknown as vscode.DebugSession,
            stopSession: resourceStop,
        }];

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /Resource stop failed/);

        assert.strictEqual(aspireDebugSession.isShuttingDown, true);
        assert.strictEqual(aspireDebugSession.isStopAttemptInProgress, false);
        const presentations = [];
        for (const browserType of ['integratedBrowser', 'openExternalBrowser', 'debugEdge'] as const) {
            presentations.push(await aspireDebugSession.openDashboard('https://localhost:1234', browserType));
        }

        assert.deepStrictEqual(presentations, [undefined, undefined, undefined]);
        sinon.assert.notCalled(executeCommand);
        sinon.assert.notCalled(openExternal);
        sinon.assert.notCalled(onDidStartDebugSession);
        sinon.assert.notCalled(startDebugging);

        resourceStop.resetBehavior();
        resourceStop.resolves();
        await aspireDebugSession.stopDebugging();
        sinon.assert.calledTwice(resourceStop);
    });

    test('does not record a dashboard failure when external fallback succeeds', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.env, 'openExternal').resolves(true);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        const presentation = await aspireDebugSession.openDashboard('https://localhost:1234/private', 'debugEdge', true);

        assert.strictEqual(presentation, 'externalBrowser');
        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('records one dashboard failure when external fallback declines', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.env, 'openExternal').resolves(false);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234/private', 'debugEdge', true);

        const records = readLatestLaunchFailures(appHostPath);
        assert.strictEqual(records.length, 1);
        assert.deepStrictEqual(getFailureDetails(records[0]), {
            stage: 'dashboard',
            category: 'unknown',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
    });

    test('records one final dashboard failure when external fallback throws', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const dashboardUrl = 'https://localhost:1234/private?token=secret';
        const rawError = Object.assign(new Error(`Failed to open ${dashboardUrl}`), { code: 'EACCES' });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.env, 'openExternal').rejects(rawError);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await assert.rejects(() => aspireDebugSession.openDashboard(dashboardUrl, 'debugChrome', true), rawError);

        const records = readLatestLaunchFailures(appHostPath);
        assert.strictEqual(records.length, 1);
        assert.deepStrictEqual(getFailureDetails(records[0]), {
            stage: 'dashboard',
            category: 'permissionDenied',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(records);
        assert.strictEqual(serialized.includes(dashboardUrl), false);
        assert.strictEqual(serialized.includes(rawError.message), false);
    });

    test('records external dashboard open declines', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.env, 'openExternal').resolves(false);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await aspireDebugSession.openDashboard('https://localhost:1234/private', 'openExternalBrowser');

        const records = readLatestLaunchFailures(appHostPath);
        assert.strictEqual(records.length, 1);
        assert.deepStrictEqual(getFailureDetails(records[0]), {
            stage: 'dashboard',
            category: 'unknown',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
    });

    test('records dashboard debug-browser start exceptions without retaining the URL or error', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const dashboardUrl = 'https://localhost:1234/private?token=secret';
        const rawError = new Error(`Failed to open ${dashboardUrl}`);
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').throws(rawError);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await assert.rejects(() => aspireDebugSession.openDashboard(dashboardUrl, 'debugChrome', true), rawError);

        const records = readLatestLaunchFailures(appHostPath);
        assert.deepStrictEqual(getFailureDetails(records[0]), {
            stage: 'dashboard',
            category: 'unknown',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(records);
        assert.ok(!serialized.includes(dashboardUrl));
        assert.ok(!serialized.includes(rawError.message));
    });

    test('records integrated dashboard open exceptions', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const rawError = new Error('raw-integrated-browser-error');
        sinon.stub(vscode.commands, 'executeCommand').rejects(rawError);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await assert.rejects(() => aspireDebugSession.openDashboard('https://localhost:1234', 'integratedBrowser'), rawError);

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'dashboard',
            category: 'unknown',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
    });

    test('records external dashboard open exceptions from stable error codes', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const rawError = Object.assign(new Error('raw-external-browser-error'), { code: 'EACCES' });
        sinon.stub(vscode.env, 'openExternal').rejects(rawError);
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });

        await assert.rejects(() => aspireDebugSession.openDashboard('https://localhost:1234', 'openExternalBrowser'), rawError);

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'dashboard',
            category: 'permissionDenied',
            controller: 'editor',
            mode: 'run',
            providerKind: 'browser',
            exitCodeBucket: 'none',
        });
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

    test('stopDebugging reserves budget for the AppHost stop when a resource adapter never acknowledges', async () => {
        // A debug adapter suspended at a breakpoint does not acknowledge stopDebugging() until its
        // runtime resumes, so it consumes whatever budget it is given. When every phase shared one
        // deadline, that left the AppHost stop with 0ms: it timed out immediately, the AppHost
        // process kept running, and its resources stayed in the Call Stack pane after the debug
        // session disappeared. The reserve exists so the AppHost and parent stops still land.
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.java',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const appHostDebugSession = {
            id: 'apphost-session',
            type: 'java',
            name: 'AppHost',
            configuration: { type: 'java', request: 'launch', name: 'AppHost' },
        };
        const suspendedResourceDebugSession = {
            id: 'suspended-resource-session',
            type: 'java',
            name: 'catalog',
            configuration: { type: 'java', request: 'attach', name: 'catalog' },
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };

        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').callsFake(async session => {
            if (session === (suspendedResourceDebugSession as unknown as vscode.DebugSession)) {
                // Never settles, exactly like an adapter stopped at a breakpoint.
                return new Promise<void>(() => { });
            }

            if (session === (appHostDebugSession as unknown as vscode.DebugSession)) {
                // Deliberately non-instant. A stop that resolves in the same microtask would win the
                // race even against a 0ms budget, so the test would pass without the reserve.
                await new Promise<void>(resolve => setTimeout(resolve, 50));
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
                id: suspendedResourceDebugSession.id,
                session: suspendedResourceDebugSession as unknown as vscode.DebugSession,
                stopSession: () => vscode.debug.stopDebugging(suspendedResourceDebugSession as unknown as vscode.DebugSession),
            },
        ];

        // The suspended resource still times out, and that failure has to reach the caller.
        await assert.rejects(() => aspireDebugSession.stopDebugging());

        assert.strictEqual((aspireDebugSession as any)._appHostStopped, true, 'AppHost stop must be confirmed even when a resource adapter never acknowledges');
        assert.strictEqual((aspireDebugSession as any)._parentStopped, true, 'Aspire parent stop must be confirmed even when a resource adapter never acknowledges');
        assert.ok(stopDebuggingStub.calledWith(appHostDebugSession as unknown as vscode.DebugSession));
        assert.ok(stopDebuggingStub.calledWith(parentDebugSession as unknown as vscode.DebugSession));
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
        const stopCli = sinon.stub().resolves();
        (aspireDebugSession as any)._rpcClient = { stopCli };
        const scheduleCliProcessTermination = sinon.stub(aspireDebugSession as any, 'scheduleCliProcessTermination');
        (aspireDebugSession as any)._appHostDebugSession = {
            id: appHostDebugSession.id,
            session: appHostDebugSession as unknown as vscode.DebugSession,
            stopSession: () => vscode.debug.stopDebugging(appHostDebugSession as unknown as vscode.DebugSession),
        };

        await assert.rejects(() => aspireDebugSession.stopDebugging(), /AppHost stop failed/);

        assert.strictEqual(stopDebuggingStub.callCount, 2);
        assert.strictEqual(stopDebuggingStub.firstCall.args[0], appHostDebugSession);
        assert.strictEqual(stopDebuggingStub.secondCall.args[0], parentDebugSession);
        sinon.assert.calledOnce(stopCli);
        sinon.assert.calledOnce(scheduleCliProcessTermination);
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

        await assert.rejects(shutdown, /Timed out after 6 seconds waiting for debug session 'Resource' to start/);

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
        // Just short of the resource budget the shutdown is still waiting on the resource, so nothing
        // else has been stopped yet: the ordering is honoured right up to the resource deadline.
        await clock.tickAsync(5_000);
        assert.strictEqual(stopDebuggingStub.callCount, 0, 'The AppHost must not be stopped while a resource stop is still within budget');

        await clock.tickAsync(2_000);

        await assert.rejects(stopPromise, (err: Error) => {
            // Six, not ten: the resource phase runs against the reserved deadline so the AppHost and
            // parent stops still have a usable budget after a wedged resource consumes its own.
            assert.strictEqual(err.message, debugSessionStopTimedOut('Wedged resource', 6));
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

        await assert.rejects(stopPromise, (err: AggregateError) => {
            // Both phases time out, and the budgets they report are the point of this test. The
            // resource is bounded at the reserved deadline rather than the whole budget, which is
            // what leaves the AppHost a real four seconds instead of the one second it used to get
            // after a slow resource had taken everything else.
            assert.deepStrictEqual(
                (err.errors as Error[]).map(error => error.message),
                [debugSessionStopTimedOut('Slow resource', 6), debugSessionStopTimedOut('AppHost', 4)]);
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
        await assert.rejects(firstStop, /Timed out after 6 seconds waiting for debug session 'Resource' to stop/);

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

    test('records a pre-start process exit when VS Code sends a cleanup disconnect before adapter termination', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.mts');
        writeFileSync(appHostPath, '');
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        let adapterTrackerFactory: vscode.DebugAdapterTrackerFactory | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').callsFake((_debugType, factory) => {
            adapterTrackerFactory = factory;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const appHostConfiguration = {
            type: 'pwa-node',
            name: 'AppHost',
            request: 'launch',
            runId: 'apphost-run',
            debugSessionId: aspireDebugSession.debugSessionId,
            isApphost: true,
            noDebug: false,
        } as AspireResourceExtendedDebugConfiguration;
        const appHostVsCodeSession = {
            id: 'apphost-session',
            type: 'pwa-node',
            name: 'AppHost',
            configuration: appHostConfiguration,
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: appHostVsCodeSession.id,
            session: appHostVsCodeSession,
            stopSession: sinon.stub().resolves(),
        };
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves(appHostConfiguration);
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostDebugSession);
        sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        await aspireDebugSession.startAppHost(appHostPath, [], [], true, { forceBuild: false });

        const adapterTracker = adapterTrackerFactory!.createDebugAdapterTracker(appHostVsCodeSession) as vscode.DebugAdapterTracker;
        adapterTracker.onWillReceiveMessage!({
            type: 'request',
            seq: 1,
            command: 'disconnect',
            arguments: { terminateDebuggee: false },
        });
        adapterTracker.onDidSendMessage!({
            type: 'event',
            event: 'terminated',
            body: {},
        });
        await terminateCallback!(appHostVsCodeSession);

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'dcpStartup',
            category: 'processExited',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'node',
            exitCodeBucket: 'none',
        });
    });

    test('attributes folder-based launch failures to the concrete AppHost project', async () => {
        const workspacePath = makeTempDir();
        const appHostPath = join(workspacePath, 'AppHost', 'AppHost.csproj');
        mkdirSync(dirname(appHostPath), { recursive: true });
        writeFileSync(appHostPath, '');
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: workspacePath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const appHostVsCodeSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { noDebug: false },
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: appHostVsCodeSession.id,
            session: appHostVsCodeSession,
            stopSession: sinon.stub().resolves(),
        };
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return { dispose: sinon.stub() };
        });
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            name: 'AppHost',
            request: 'launch',
            runId: '',
        } as AspireResourceExtendedDebugConfiguration);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostDebugSession);
        sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        await aspireDebugSession.startAppHost(appHostPath, [], [], true, { forceBuild: false });

        await terminateCallback!(appHostVsCodeSession);

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'dcpStartup',
            category: 'processExited',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
        assert.deepStrictEqual(readLatestLaunchFailures(workspacePath), []);
    });

    test('does not record a pre-start process exit when an explicit disconnect precedes adapter termination', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.mts');
        writeFileSync(appHostPath, '');
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        let adapterTrackerFactory: vscode.DebugAdapterTrackerFactory | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').callsFake((_debugType, factory) => {
            adapterTrackerFactory = factory;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const appHostConfiguration = {
            type: 'pwa-node',
            name: 'AppHost',
            request: 'launch',
            runId: 'apphost-run',
            debugSessionId: aspireDebugSession.debugSessionId,
            isApphost: true,
            noDebug: false,
        } as AspireResourceExtendedDebugConfiguration;
        const appHostVsCodeSession = {
            id: 'apphost-session',
            type: 'pwa-node',
            name: 'AppHost',
            configuration: appHostConfiguration,
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: appHostVsCodeSession.id,
            session: appHostVsCodeSession,
            stopSession: sinon.stub().resolves(),
        };
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves(appHostConfiguration);
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostDebugSession);
        sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        await aspireDebugSession.startAppHost(appHostPath, [], [], true, { forceBuild: false });

        const adapterTracker = adapterTrackerFactory!.createDebugAdapterTracker(appHostVsCodeSession) as vscode.DebugAdapterTracker;
        adapterTracker.onWillReceiveMessage!({
            type: 'request',
            seq: 1,
            command: 'disconnect',
            arguments: { terminateDebuggee: true },
        });
        adapterTracker.onDidSendMessage!({
            type: 'event',
            event: 'terminated',
            body: {},
        });
        await terminateCallback!(appHostVsCodeSession);

        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('does not record DCP startup failure after startup completion', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        writeFileSync(appHostPath, '');
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const appHostVsCodeSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { noDebug: true },
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: appHostVsCodeSession.id,
            session: appHostVsCodeSession,
            stopSession: sinon.stub().resolves(),
        };
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return { dispose: sinon.stub() };
        });
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            name: 'AppHost',
            request: 'launch',
            runId: '',
        } as AspireResourceExtendedDebugConfiguration);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostDebugSession);
        sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        await aspireDebugSession.startAppHost(appHostPath, [], [], false, { forceBuild: false });
        aspireDebugSession.notifyAppHostStartupCompleted();

        await terminateCallback!(appHostVsCodeSession);

        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('does not record a successful pipeline AppHost exit as a DCP startup failure', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        writeFileSync(appHostPath, '');
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'publish',
            },
        } as unknown as vscode.DebugSession;
        const appHostVsCodeSession = {
            id: 'apphost-session',
            type: 'coreclr',
            name: 'AppHost',
            configuration: { noDebug: true },
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: appHostVsCodeSession.id,
            session: appHostVsCodeSession,
            stopSession: sinon.stub().resolves(),
        };
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return { dispose: sinon.stub() };
        });
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'coreclr',
            name: 'AppHost',
            request: 'launch',
            runId: '',
        } as AspireResourceExtendedDebugConfiguration);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { },
            () => { },
            'debug-1',
            'publish');
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostDebugSession);
        sinon.stub(aspireDebugSession as any, 'stopDebuggingInBackground');
        await aspireDebugSession.startAppHost(appHostPath, [], [], false, { forceBuild: false });

        await terminateCallback!(appHostVsCodeSession);

        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('records typed AppHost build failures as build failures', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.csproj');
        writeFileSync(appHostPath, '');
        const outputPath = join(dirname(appHostPath), 'bin', 'AppHost.dll');
        const rawBuildOutput = 'raw-build-output';
        sinon.stub(childProcess, 'execFile').yields(null, {
            stdout: JSON.stringify({
                Properties: {
                    TargetPath: outputPath,
                    RunCommand: '',
                    UseAppHost: 'false',
                    UseWinUI: 'false',
                    WindowsPackageType: '',
                },
            }),
            stderr: '',
        });
        const buildProcess = Object.assign(new EventEmitter(), {
            stdout: new PassThrough(),
            stderr: new PassThrough(),
        });
        sinon.stub(childProcess, 'spawn').callsFake(() => {
            setImmediate(() => {
                buildProcess.stdout.write(rawBuildOutput);
                buildProcess.emit('close', 1);
            });
            return buildProcess as unknown as childProcess.ChildProcessWithoutNullStreams;
        });
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
                noDebug: false,
            },
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');

        await aspireDebugSession.startAppHost(appHostPath, [], [], true, { forceBuild: true });

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'build',
            category: 'buildFailed',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
        const serialized = JSON.stringify(readLatestLaunchFailures(appHostPath));
        assert.strictEqual(serialized.includes(rawBuildOutput), false);
        assert.strictEqual(serialized.includes(appHostPath), false);
    });

    test('records typed Rust build failures without raw Cargo data in the journal or telemetry', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.rs');
        writeFileSync(appHostPath, '');
        const sentinels = [
            appHostPath,
            'RAW_CARGO_STDOUT_SENTINEL',
            'RAW_CARGO_STDERR_SENTINEL',
            'CARGO_CREDENTIAL_SENTINEL',
            'cargo-token-sentinel',
        ] as const;
        const rawCargoFailure = [
            sentinels[0],
            sentinels[1],
            sentinels[2],
            sentinels[3],
            `https://registry.example.invalid/index?t=${sentinels[4]}`,
        ].join(' | ');
        const fake = new FakeTelemetryReporter();
        const restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        try {
            sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
                extensionId === 'ms-vscode.cpptools' || extensionId === 'vadimcn.vscode-lldb'
                    ? { id: extensionId } as vscode.Extension<unknown>
                    : undefined);
            sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration')
                .rejects(new AppHostBuildFailureError(rawCargoFailure, true));
            sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
            sinon.stub(vscode.debug, 'stopDebugging').resolves();
            const parentDebugSession = {
                id: 'aspire-session',
                type: 'aspire',
                name: 'Aspire',
                configuration: {
                    type: 'aspire',
                    request: 'launch',
                    name: 'Aspire',
                    program: appHostPath,
                    command: 'run',
                    noDebug: false,
                },
            } as unknown as vscode.DebugSession;
            const aspireDebugSession = new AspireDebugSession(
                parentDebugSession,
                {} as any,
                {} as any,
                {} as any,
                () => { });
            sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');

            await aspireDebugSession.startAppHost(
                appHostPath,
                ['cargo', 'run'],
                [],
                true,
                { forceBuild: false });

            const failures = readLatestLaunchFailures(appHostPath);
            assert.strictEqual(failures.length, 1);
            assert.deepStrictEqual(getFailureDetails(failures[0]), {
                stage: 'build',
                category: 'buildFailed',
                controller: 'editor',
                mode: 'debug',
                providerKind: 'rust',
                exitCodeBucket: 'none',
            });
            const launchFailureEvents = fake.events.filter(
                event => event.name === 'aspire/vscode/launchfailure/recorded');
            assert.strictEqual(launchFailureEvents.length, 1);
            assert.deepStrictEqual(launchFailureEvents[0].properties, {
                stage: 'build',
                category: 'buildFailed',
                controller: 'editor',
                mode: 'debug',
                provider_kind: 'rust',
                exit_code_bucket: 'none',
            });

            const serialized = JSON.stringify({
                journal: failures,
                telemetry: fake.events,
            });
            for (const sentinel of sentinels) {
                assert.strictEqual(serialized.includes(sentinel), false, `Leaked sentinel: ${sentinel}`);
            }
        }
        finally {
            restoreReporter();
        }
    });

    test('records untyped AppHost configuration failures as debug-session failures', async () => {
        const appHostPath = join(makeTempDir(), 'apphost.ts');
        writeFileSync(appHostPath, '');
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').rejects(new Error('raw-configuration-error'));
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
                noDebug: true,
            },
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');

        await aspireDebugSession.startAppHost(appHostPath, [], [], false, { forceBuild: false });

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'run',
            providerKind: 'node',
            exitCodeBucket: 'none',
        });
    });

    test('records Java AppHost configuration failures with the Java provider kind', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.java');
        writeFileSync(appHostPath, 'class AppHost {}');
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            extensionId === 'vscjava.vscode-java-debug'
                ? { id: extensionId } as vscode.Extension<unknown>
                : undefined);
        sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration')
            .rejects(new Error('raw-java-configuration-error'));
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
                noDebug: false,
            },
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');

        await aspireDebugSession.startAppHost(
            appHostPath,
            ['java', '-cp', 'target/classes', 'AppHost'],
            [],
            true,
            { forceBuild: false });

        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'java',
            exitCodeBucket: 'none',
        });
    });

    test('hands a Deno TypeScript AppHost to the node debugger', async () => {
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
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration').resolves({
            type: 'pwa-node',
            request: 'launch',
            name: 'Deno AppHost',
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
            ['deno', 'run', '-A', '--unstable-sloppy-imports', appHostPath],
            [],
            true,
            { forceBuild: false });

        const launchConfig = createDebugSessionConfiguration.firstCall.args[1] as NodeLaunchConfiguration;
        const appHostArgs = createDebugSessionConfiguration.firstCall.args[2];
        const debuggerExtension = createDebugSessionConfiguration.firstCall.args[5];

        assert.deepStrictEqual(launchConfig, {
            script_path: appHostPath,
            working_directory: dirname(appHostPath),
            type: 'node',
            runtime_executable: 'deno',
        });
        assert.deepStrictEqual(appHostArgs, ['run', '-A', '--unstable-sloppy-imports', appHostPath]);
        assert.strictEqual(debuggerExtension.resourceType, 'node');
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

    test('launches a Java AppHost with the Java debugger', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.java');
        writeFileSync(appHostPath, '');
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            extensionId === 'vscjava.vscode-java-debug'
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
            type: 'java',
            request: 'launch',
            name: 'Java AppHost',
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

        // The CLI sends the launcher it would have run itself, so the classpath the build tool staged is
        // already resolved and the adapter never has to reproduce it. Those entries are relative to the
        // AppHost directory, because that is the working directory the CLI runs `java` from.
        const classPath = ['target/classes', 'target/aspire-deps/*'].join(pathDelimiter);

        await aspireDebugSession.startAppHost(
            appHostPath,
            ['java', '-Xmx512m', '-cp', classPath, 'AppHost', '--example-argument'],
            [],
            true,
            { forceBuild: false });

        const launchConfig = createDebugSessionConfiguration.firstCall.args[1] as JavaLaunchConfiguration;
        const appHostArgs = createDebugSessionConfiguration.firstCall.args[2];
        const debuggerExtension = createDebugSessionConfiguration.firstCall.args[5];

        // Absolute, not the relative entries the CLI sent: the adapter does not resolve classPaths
        // against the launch configuration's cwd, so relative entries are looked up somewhere else
        // entirely and the JVM starts without the AppHost class on its classpath.
        assert.deepStrictEqual(launchConfig, {
            type: 'java',
            main_class: 'AppHost',
            class_paths: [
                join(dirname(appHostPath), 'target', 'classes'),
                join(dirname(appHostPath), 'target', 'aspire-deps', '*')
            ],
            working_directory: dirname(appHostPath),
            vm_args: ['-Xmx512m'],
        });
        assert.deepStrictEqual(appHostArgs, ['--example-argument']);
        assert.strictEqual(debuggerExtension.resourceType, 'java');
    });

    test('omits vm_args from a Java AppHost launch when the command carries none', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.java');
        writeFileSync(appHostPath, '');
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            extensionId === 'vscjava.vscode-java-debug'
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
            type: 'java',
            request: 'launch',
            name: 'Java AppHost',
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
            ['java', '-cp', 'target/classes', 'AppHost'],
            [],
            true,
            { forceBuild: false });

        const launchConfig = createDebugSessionConfiguration.firstCall.args[1] as JavaLaunchConfiguration;

        // An empty vm_args array is not the same as no vm_args to the Java adapter, and build_tool is
        // deliberately absent because the classpath is supplied explicitly.
        assert.deepStrictEqual(launchConfig, {
            type: 'java',
            main_class: 'AppHost',
            class_paths: [join(dirname(appHostPath), 'target', 'classes')],
            working_directory: dirname(appHostPath),
        });
    });

    test('reports the missing Java debugger extension rather than an unsupported debug type', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.java');
        writeFileSync(appHostPath, '');
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

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
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        await aspireDebugSession.startAppHost(
            appHostPath,
            ['java', '-cp', 'target/classes', 'AppHost'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(startAndGetDebugSession.called, false);
        const message = showErrorMessage.firstCall.args[0] as string;
        assert.ok(
            message.includes('vscjava.vscode-java-debug'),
            `expected an actionable install message, got ${message}`);
    });

    test('reports an unrecognised Java AppHost command instead of guessing a launch', async () => {
        const appHostPath = join(makeTempDir(), 'AppHost.java');
        writeFileSync(appHostPath, '');
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            extensionId === 'vscjava.vscode-java-debug'
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
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        // A wrapper invocation has no main class to hand the adapter, and guessing one would start a JVM
        // with the wrong arguments.
        const secret = 'database-password';
        await aspireDebugSession.startAppHost(
            appHostPath,
            ['./mvnw', `-Dspring.datasource.password=${secret}`, 'exec:java'],
            [],
            true,
            { forceBuild: false });

        assert.strictEqual(startAndGetDebugSession.called, false);
        const message = showErrorMessage.firstCall.args[0] as string;
        assert.ok(!message.includes(secret), `expected the rejected command arguments to be omitted, got ${message}`);
    });

    test('an AppHost restart preserves the target-scoped CLI path and argument tokens', async () => {
        const pathResolvedCli = await cliPathModule.findCliOnPath({
            platform: 'linux',
            pathValue: '/selected/bin',
            fileExists: async candidate => candidate === '/selected/bin/aspire',
            tryExecute: async candidate => candidate === '/selected/bin/aspire',
        });
        assert.strictEqual(pathResolvedCli, '/selected/bin/aspire');

        let restartHandler: ((debugSessionId: string) => boolean) | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => unknown) | undefined;
        const forwardedArgs = ['--isolated', '--', '--app-option', 'value with spaces'];
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
                args: forwardedArgs,
                resolvedCliPath: pathResolvedCli,
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
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore').callsFake((_debugAdapter, options) => {
            restartHandler = options?.onRestartRequested;
        });
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostResourceSession);
        sinon.stub(aspireDebugSession, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/AppHost.csproj', ['run'], [], true, { forceBuild: false });
        assert.strictEqual(restartHandler?.(aspireDebugSession.debugSessionId), true);
        await terminateSessionCallback?.(appHostDebugSession as unknown as vscode.DebugSession);

        const restartedConfig = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(restartedConfig.resolvedCliPath, pathResolvedCli);
        assert.deepStrictEqual(restartedConfig.args, forwardedArgs);
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(restartedConfig), false);

        const tryExecuteCliStub = sinon.stub(cliPathModule, 'tryExecuteCli').resolves(true);
        const reservedPaths: string[] = [];
        const preparedCliPaths: Array<string | undefined> = [];
        const provider = new AspireDebugConfigurationProvider({
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => undefined,
        } as unknown as AppHostDiscoveryService, {
            tryReserveExternalLaunch: (appHostPath: string) => {
                reservedPaths.push(appHostPath);
                return 'restart-reservation';
            },
            validateOrReacquireExternalLaunchReservation: () => {
                throw new Error('The restart should acquire a fresh reservation.');
            },
            replaceExternalLaunchReservation: () => {
                throw new Error('The restart should acquire a fresh reservation.');
            },
            releaseExternalLaunchReservation: () => {
                throw new Error('The successful restart should keep its reservation.');
            },
            markLaunchAttemptFailureRecorded: () => {
                throw new Error('The successful restart should not record a launch failure.');
            },
            tryReserveExternalOperation: () => {
                throw new Error('The run restart should not reserve an external operation.');
            },
            validateOrReacquireExternalOperationReservation: () => {
                throw new Error('The run restart should not validate an external operation.');
            },
            replaceExternalOperationReservation: () => {
                throw new Error('The run restart should not replace an external operation.');
            },
            releaseExternalOperationReservation: () => {
                throw new Error('The run restart should not release an external operation.');
            },
            prepareLaunchArguments: async (_appHostPath: string, _command: string, args: string[] | undefined, _token: vscode.CancellationToken, cliPath?: string) => {
                preparedCliPaths.push(cliPath);
                return { args };
            },
        }, {
            get: () => undefined,
            update: async () => { },
            keys: () => [],
        });
        const checkedConfig = await provider.resolveDebugConfiguration(undefined, restartedConfig);
        assert.strictEqual(checkedConfig, restartedConfig);
        sinon.assert.calledOnceWithExactly(tryExecuteCliStub, pathResolvedCli);

        const resolvedConfig = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, restartedConfig) as AspireExtendedDebugConfiguration | undefined;

        assert.deepStrictEqual(reservedPaths, ['/workspace/apphost.cs']);
        assert.deepStrictEqual(preparedCliPaths, [pathResolvedCli]);
        assert.strictEqual(resolvedConfig?.[appHostLaunchReservationIdConfigKey], 'restart-reservation');
        assert.strictEqual(resolvedConfig?.resolvedCliPath, pathResolvedCli);
        assert.deepStrictEqual(resolvedConfig?.args, forwardedArgs);

        const replacementCliProcess = createFakeCliProcess(3391);
        const spawnCliStub = sinon.stub(cliModule, 'spawnCliProcess').returns(replacementCliProcess);
        const getAspireCliExecutablePath = sinon.stub().rejects(new Error('The trusted restart pin should be used.'));
        const replacementSession = new AspireDebugSession(
            {
                id: 'replacement-aspire-session',
                configuration: resolvedConfig,
            } as unknown as vscode.DebugSession,
            { onNewConnection: () => ({ dispose: () => { } }) } as any,
            { recordAppHostProcessExit: () => { } } as any,
            {
                getAspireCliExecutablePath,
                createEnvironment: () => ({}),
            } as any,
            () => { });

        await replacementSession.spawnAspireCommand(['run'], '/workspace', false, 'aspire run');

        assert.strictEqual(spawnCliStub.firstCall.args[1], pathResolvedCli);
        assert.strictEqual(getAspireCliExecutablePath.called, false);
    });

    test('a service-owned non-Run restart reclaims its pending operation through the provider', async () => {
        let restartHandler: ((debugSessionId: string) => boolean) | undefined;
        let appHostTrackerDebugSessionId: string | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => unknown) | undefined;
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire deploy',
                program: '/workspace/apphost.cs',
                command: 'deploy',
                [appHostLaunchTokenConfigKey]: 42,
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
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore').callsFake((_debugAdapter, appHostTracker) => {
            appHostTrackerDebugSessionId = appHostTracker?.debugSessionId;
            restartHandler = appHostTracker?.onRestartRequested;
        });
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(appHostResourceSession);
        sinon.stub(aspireDebugSession, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/AppHost.csproj', ['run'], [], true, { forceBuild: false });
        assert.strictEqual(appHostTrackerDebugSessionId, aspireDebugSession.debugSessionId);
        assert.strictEqual(restartHandler?.(aspireDebugSession.debugSessionId), true);
        await terminateSessionCallback?.(appHostDebugSession as unknown as vscode.DebugSession);

        const restartedConfig = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(restartedConfig), true);

        const provider = new AspireDebugConfigurationProvider({
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => undefined,
        } as unknown as AppHostDiscoveryService, {
            tryReserveExternalLaunch: () => {
                throw new Error('The non-Run restart should not reserve an external launch.');
            },
            validateOrReacquireExternalLaunchReservation: () => {
                throw new Error('The non-Run restart should not validate an external launch.');
            },
            replaceExternalLaunchReservation: () => {
                throw new Error('The non-Run restart should not replace an external launch.');
            },
            releaseExternalLaunchReservation: () => {
                throw new Error('The non-Run restart should not release an external launch.');
            },
            markLaunchAttemptFailureRecorded: () => {
                throw new Error('The successful restart should not record a launch failure.');
            },
            tryReserveExternalOperation: () => {
                throw new Error('The matching launch token already owns this operation.');
            },
            validateOrReacquireExternalOperationReservation: () => {
                throw new Error('The matching launch token already owns this operation.');
            },
            replaceExternalOperationReservation: () => {
                throw new Error('The matching launch token already owns this operation.');
            },
            releaseExternalOperationReservation: () => {
                throw new Error('The matching launch token already owns this operation.');
            },
            prepareLaunchArguments: async (_appHostPath: string, _command: string, args: string[] | undefined) => ({ args }),
        }, {
            get: () => undefined,
            update: async () => { },
            keys: () => [],
        });

        const resolvedConfig = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, restartedConfig);

        assert.strictEqual(resolvedConfig, restartedConfig);
        assert.strictEqual(resolvedConfig?.[appHostLaunchTokenConfigKey], 42);
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
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore').callsFake((_debugAdapter, options) => {
            restartHandler = options?.onRestartRequested;
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

    test('redacts debug configuration arguments when environment logging is disabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: '.NET',
            request: 'launch',
            args: ['--api-key', 'secret-value'],
            runtimeArgs: ['--runtime-secret'],
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, false);

        assert.strictEqual(loggableConfig.args, '<redacted>');
        assert.strictEqual(loggableConfig.runtimeArgs, '<redacted>');
        assert.deepStrictEqual(debugConfig.args, ['--api-key', 'secret-value']);
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['--runtime-secret']);
    });

    test('redacts debug configuration arguments without mutating the source when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'pwa-node',
            name: 'Node.js package script',
            request: 'launch',
            runtimeExecutable: 'npm',
            runtimeArgs: ['run', 'start', '--', '--api-key', 'runtime-secret'],
            args: ['--api-key', 'app-secret'],
            env: {
                LOG_LEVEL: 'debug',
            },
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.notStrictEqual(loggableConfig, debugConfig);
        assert.strictEqual(loggableConfig.args, '<redacted>');
        assert.strictEqual(loggableConfig.runtimeArgs, '<redacted>');
        assert.deepStrictEqual(loggableConfig.env, { LOG_LEVEL: 'debug' });
        assert.deepStrictEqual(debugConfig.args, ['--api-key', 'app-secret']);
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', 'start', '--', '--api-key', 'runtime-secret']);
    });

    test('redacts MAUI environmentVariables even when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            args: ['--api-key', 'app-secret'],
            runtimeArgs: ['--runtime-secret'],
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.deepStrictEqual(loggableConfig.env, { SECRET_TOKEN: 'env-secret' });
        assert.strictEqual(loggableConfig.environmentVariables, '<redacted>');
        assert.strictEqual(loggableConfig.args, '<redacted>');
        assert.strictEqual(loggableConfig.runtimeArgs, '<redacted>');
    });

    test('redacts Java vmArgs and classPaths by default', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'java',
            name: 'catalog',
            request: 'launch',
            // A JVM system property is the ordinary way a secret reaches a Java process, and the
            // extension log persists to disk.
            vmArgs: ['-Xmx512m', '-Dspring.datasource.password=hunter2'],
            classPaths: ['/Users/someone/src/private-project/target/classes'],
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, false);

        assert.strictEqual(loggableConfig.vmArgs, '<redacted>');
        assert.strictEqual(loggableConfig.classPaths, '<redacted: 1 entry>');
        const serialized = JSON.stringify(loggableConfig);
        assert.ok(!serialized.includes('hunter2'));
        assert.ok(!serialized.includes('/Users/someone'));
    });

    test('includes Java vmArgs and classPaths when debug configuration logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'java',
            name: 'catalog',
            request: 'launch',
            vmArgs: ['-Xmx512m'],
            classPaths: ['/Users/someone/src/project/target/classes', '/deps/a.jar'],
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.deepStrictEqual(loggableConfig.vmArgs, ['-Xmx512m']);
        assert.deepStrictEqual(loggableConfig.classPaths, ['/Users/someone/src/project/target/classes', '/deps/a.jar']);
    });

    test('redacts sensitive debugger environments even when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'lldb',
            name: 'Rust',
            request: 'launch',
            args: ['--api-key', 'app-secret'],
            runtimeArgs: ['--runtime-secret'],
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
        assert.strictEqual(loggableConfig.args, '<redacted>');
        assert.strictEqual(loggableConfig.runtimeArgs, '<redacted>');
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
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures('/workspace/MauiAppHost/MauiAppHost.csproj')[0]), {
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'none',
        });
    });

    test('records resource debug-session start exceptions', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'debugpy',
            name: 'Python',
            request: 'launch',
            noDebug: false,
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').rejects(new Error('raw-resource-start-error'));
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });

        const result = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.strictEqual(result, undefined);
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'python',
            exitCodeBucket: 'none',
        });
    });

    test('does not record a resource debug-session start decline after shutdown begins', async () => {
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'debugpy',
            name: 'Python',
            request: 'launch',
            noDebug: false,
        } as AspireResourceExtendedDebugConfiguration;
        let resolveStart!: (started: boolean) => void;
        const start = new Promise<boolean>(resolve => {
            resolveStart = resolve;
        });
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').returns(start);
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });

        const resultPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        (aspireDebugSession as any)._stopping = true;
        resolveStart(false);

        assert.strictEqual(await resultPromise, undefined);
        assert.deepStrictEqual(readLatestLaunchFailures(appHostPath), []);
    });

    test('records resource debug-session start timeouts at the timeout boundary', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const appHostPath = '/workspace/AppHost/AppHost.csproj';
        const parentDebugSession = {
            id: 'aspire-session',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'go',
            name: 'Go',
            request: 'launch',
            noDebug: true,
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').returns({ dispose: sinon.stub() });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });

        const resultPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await clock.tickAsync(10_000);
        const result = await resultPromise;

        assert.strictEqual(result, undefined);
        assert.deepStrictEqual(getFailureDetails(readLatestLaunchFailures(appHostPath)[0]), {
            stage: 'debugSession',
            category: 'timeout',
            controller: 'editor',
            mode: 'run',
            providerKind: 'go',
            exitCodeBucket: 'none',
        });
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

    test('tracks only safe resource target lifecycle snapshots', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let terminateSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        const startGate = createDeferred<boolean>();
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
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'Project',
            request: 'launch',
            program: '/workspace/Api/bin/Debug/net10.0/Api.dll',
            targetPath: '/workspace/Api/Api.csproj',
            resourceExecutablePaths: ['/workspace/.dotnet/dotnet'],
            noDebug: false,
        } as AspireResourceExtendedDebugConfiguration;
        const resourceSession = {
            id: 'resource-session',
            type: 'coreclr',
            name: 'Project',
            configuration: {
                ...debugConfig,
                env: { PRIVATE_TOKEN: 'secret' },
            },
        } as unknown as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startGate.promise);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });
        const appHostIdentity = aspireDebugSession.appHostIdentity;
        assert.ok(appHostIdentity);
        sinon.stub(aspireDebugSession as any, 'createDebugAdapterTrackerCore');

        const resourcePromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        assert.deepStrictEqual(aspireDebugSession.editorResourceSessions, [{
            appHostPath: '/workspace/AppHost/AppHost.csproj',
            appHostIdentity,
            targetPath: '/workspace/Api/Api.csproj',
            resourceExecutablePaths: ['/workspace/.dotnet/dotnet'],
            state: 'starting',
            mode: 'debug',
        }]);

        startSessionCallback?.(resourceSession);
        startGate.resolve(true);
        const resource = await resourcePromise;
        assert.ok(resource);
        assert.deepStrictEqual(aspireDebugSession.editorResourceSessions, [{
            appHostPath: '/workspace/AppHost/AppHost.csproj',
            appHostIdentity,
            targetPath: '/workspace/Api/Api.csproj',
            resourceExecutablePaths: ['/workspace/.dotnet/dotnet'],
            state: 'running',
            mode: 'debug',
        }]);

        const stop = resource.stopSession();
        assert.deepStrictEqual(aspireDebugSession.editorResourceSessions, [{
            appHostPath: '/workspace/AppHost/AppHost.csproj',
            appHostIdentity,
            targetPath: '/workspace/Api/Api.csproj',
            resourceExecutablePaths: ['/workspace/.dotnet/dotnet'],
            state: 'stopping',
            mode: 'debug',
        }]);

        terminateSessionCallback?.(resourceSession);
        await stop;
        assert.deepStrictEqual(aspireDebugSession.editorResourceSessions, []);
        assert.strictEqual(JSON.stringify(aspireDebugSession.editorResourceSessions).includes('secret'), false);

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
        test('appends a do step before additional command arguments', () => {
            const args = buildAspireCommandArgs('do', ['--verbose'], ['--start-debug-session', '--apphost', '/workspace/AppHost.csproj'], 'deploy');

            assert.deepStrictEqual(args, ['do', 'deploy', '--verbose', '--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);
        });

        test('appends a do step before command arguments and keeps extension arguments before the app argument separator', () => {
            const args = buildAspireCommandArgs('do', ['--verbose', '--', '--custom-arg', 'value'], ['--nologo', '--apphost', '/workspace/AppHost.csproj'], 'deploy');

            assert.deepStrictEqual(args, ['do', 'deploy', '--verbose', '--nologo', '--apphost', '/workspace/AppHost.csproj', '--', '--custom-arg', 'value']);
        });

        test('does not append a step to run arguments', () => {
            const args = buildAspireCommandArgs('run', ['--isolated'], ['--start-debug-session', '--apphost', '/workspace/AppHost.csproj'], 'deploy');

            assert.deepStrictEqual(args, ['run', '--isolated', '--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);
        });

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

    function getFailureDetails(record: LaunchFailureRecord | undefined) {
        assert.ok(record);
        return {
            stage: record.stage,
            category: record.category,
            controller: record.controller,
            mode: record.mode,
            providerKind: record.providerKind,
            exitCodeBucket: record.exitCodeBucket,
        };
    }

    async function captureLaunchCommandArgs(
        command: 'run' | 'do' | 'deploy' | 'publish',
        noDebug: boolean,
    ): Promise<string[]> {
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
                command,
                step: command === 'do' ? 'build' : undefined,
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            terminalProvider as any,
            () => { });
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug } });
        await waitFor(() => spawnStub.calledOnce);
        return spawnStub.firstCall.args[0];
    return spawnStub.firstCall.args[0];
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
