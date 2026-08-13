import * as assert from 'assert';
import fs = require('fs');
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireExtendedDebugConfiguration } from '../dcp/types';
import { appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import * as cliPathModule from '../utils/cliPath';
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

suite('AppHostLaunchService', () => {
    let service: AppHostLaunchService;
    let startDebuggingStub: sinon.SinonStub;
    let resolveCliPathStub: sinon.SinonStub;
    let onDidStartDebugSessionStub: sinon.SinonStub;
    let onDidStartDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
    let onDidTerminateDebugSessionStub: sinon.SinonStub;
    let onDidTerminateDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;

    setup(() => {
        onDidStartDebugSessionStub = sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            onDidStartDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        onDidTerminateDebugSessionStub = sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            onDidTerminateDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        service = new AppHostLaunchService();
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: true, source: 'path' });
    });

    teardown(() => {
        service.dispose();
        startDebuggingStub.restore();
        resolveCliPathStub.restore();
        onDidStartDebugSessionStub.restore();
        onDidTerminateDebugSessionStub.restore();
        onDidStartDebugSessionCallback = undefined;
        onDidTerminateDebugSessionCallback = undefined;
    });

    test('isLaunching returns false before launch', () => {
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch marks path as launching', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);
    });

    test('launch fires onDidChangeLaunchingState event', async () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(fired, true);
    });

    test('launch starts a debug session with correct configuration', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', false);

        assert.ok(startDebuggingStub.calledOnce);
        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.type, 'aspire');
        assert.strictEqual(config.request, 'launch');
        assert.strictEqual(config.program, '/repo/AppHost.csproj');
        assert.strictEqual(config.command, 'run');
        assert.strictEqual(config.noDebug, false);
        assert.strictEqual(config.step, undefined);
        assert.strictEqual(config.skipCliAvailabilityCheck, true);
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'user-selection');
    });

    test('launch includes step when doStep is provided', async () => {
        await service.launch('/repo/AppHost.csproj', 'do', true, 'deploy');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.command, 'do');
        assert.strictEqual(config.step, 'deploy');
    });

    test('launch owns CLI availability probe', async () => {
        resolveCliPathStub.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'deploy', false), vscode.CancellationError);

            assert.strictEqual(resolveCliPathStub.calledOnce, true);
            assert.strictEqual(startDebuggingStub.called, false);
        }
        finally {
            showErrorMessageStub.restore();
        }
    });

    test('clearLaunching removes the path from launching state', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);

        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('clearLaunching fires onDidChangeLaunchingState event', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });
        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(fired, true);
    });

    test('clearLaunching does not fire event when path was not launching', () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        service.clearLaunching('/repo/nonexistent.csproj');

        assert.strictEqual(fired, false);
    });

    test('clearMatchingLaunching matches project paths to AppHost source files in the same directory', async () => {
        await service.launch('/repo/AppHost/AppHost.csproj', 'run', true);

        service.clearMatchingLaunching('/repo/AppHost/Program.cs');

        assert.strictEqual(service.isLaunching('/repo/AppHost/AppHost.csproj'), false);
    });

    test('clearMatchingLaunching does not clear unrelated paths in the same directory', async () => {
        await service.launch('/repo/AppHost/First.csproj', 'run', true);
        await service.launch('/repo/AppHost/Second.csproj', 'run', true);

        service.clearMatchingLaunching('/repo/AppHost/Program.cs');

        assert.strictEqual(service.isLaunching('/repo/AppHost/First.csproj'), true);
        assert.strictEqual(service.isLaunching('/repo/AppHost/Second.csproj'), true);
    });

    test('multiple paths can be tracked independently', async () => {
        await service.launch('/repo/AppHost1.csproj', 'run', true);
        await service.launch('/repo/AppHost2.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), true);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);

        service.clearLaunching('/repo/AppHost1.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), false);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);
    });

    test('case-distinct Windows AppHosts have independent launching state', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.basename(path.dirname(String(filePath))) === 'AppHost' ? 100n : 101n,
        }) as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/apphost.mts';
        const lowerCasePath = '/workspace/apphost/apphost.mts';

        try {
            await service.launch(upperCasePath, 'run', true);

            assert.strictEqual(service.isLaunching(upperCasePath), true);
            assert.strictEqual(service.isLaunching(lowerCasePath), false);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('equivalent Windows paths reuse the same launching entry', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        let launchingStateChanges = 0;
        service.onDidChangeLaunchingState(() => launchingStateChanges++);

        try {
            await service.launch(upperCasePath, 'run', true);
            await service.launch(lowerCasePath, 'run', true);

            assert.deepStrictEqual(service.launchingPaths, [path.resolve(upperCasePath)]);
            assert.strictEqual(launchingStateChanges, 1);

            service.clearLaunching(lowerCasePath);
            assert.deepStrictEqual(service.launchingPaths, []);
            assert.strictEqual(launchingStateChanges, 2);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('failed equivalent launch keeps another launch active', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';

        try {
            await service.launch(upperCasePath, 'run', true);
            startDebuggingStub.resolves(false);

            await assert.rejects(service.launch(lowerCasePath, 'run', true), /did not start the Aspire run session/);

            assert.deepStrictEqual(service.launchingPaths, [path.resolve(upperCasePath)]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('terminated equivalent launch keeps another launch active', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';

        try {
            await service.launch(upperCasePath, 'run', true);
            await service.launch(lowerCasePath, 'run', true);
            const upperCaseConfiguration = startDebuggingStub.getCall(0).args[1] as AspireExtendedDebugConfiguration;
            const lowerCaseConfiguration = startDebuggingStub.getCall(1).args[1] as AspireExtendedDebugConfiguration;

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback({
                configuration: lowerCaseConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, [path.resolve(upperCasePath)]);

            onDidTerminateDebugSessionCallback({
                configuration: upperCaseConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, []);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('stale termination does not clear a newer equivalent launch', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';

        try {
            await service.launch(upperCasePath, 'run', true);
            const staleConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            service.clearLaunching(upperCasePath);

            await service.launch(lowerCasePath, 'run', true);
            const currentConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback({
                configuration: staleConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, [path.resolve(lowerCasePath)]);

            onDidTerminateDebugSessionCallback({
                configuration: currentConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, []);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('stale termination does not clear a newer launch after source file reconciliation', async () => {
        const projectPath = '/repo/AppHost/AppHost.csproj';
        const sourcePath = '/repo/AppHost/Program.cs';

        await service.launch(projectPath, 'run', true);
        const staleConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        service.clearMatchingLaunching(sourcePath);

        await service.launch(projectPath, 'run', true);
        const currentConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: staleConfiguration,
        } as unknown as vscode.DebugSession);
        assert.deepStrictEqual(service.launchingPaths, [path.resolve(projectPath)]);

        onDidTerminateDebugSessionCallback({
            configuration: currentConfiguration,
        } as unknown as vscode.DebugSession);
        assert.deepStrictEqual(service.launchingPaths, []);
    });

    test('suppressed equivalent launch does not clear an active launch', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const previousEnableBridge = process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE;
        const previousStateFile = process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
        const previousControlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
        const previousSuppressDebugLaunch = process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH;

        try {
            await service.launch(upperCasePath, 'run', true);
            process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE = 'true';
            process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = '/tmp/aspire-e2e-state.json';
            process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE = '/tmp/aspire-e2e-control.json';
            process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH = 'true';

            await service.launch(lowerCasePath, 'run', true);

            assert.deepStrictEqual(service.launchingPaths, [path.resolve(upperCasePath)]);
        } finally {
            setEnvironmentVariable('ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE', previousEnableBridge);
            setEnvironmentVariable('ASPIRE_EXTENSION_E2E_STATE_FILE', previousStateFile);
            setEnvironmentVariable('ASPIRE_EXTENSION_E2E_CONTROL_FILE', previousControlFile);
            setEnvironmentVariable('ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH', previousSuppressDebugLaunch);
            statStub.restore();
            platformStub.restore();
        }
    });

    test('untokened termination does not clear service-owned launching state', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'run', true);

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(service.launchingPaths, [path.resolve(appHostPath)]);
    });

    test('launch clears launching state and throws when startDebugging returns false', async () => {
        // vscode.debug.startDebugging returns Promise<boolean> and resolves false when
        // the debug adapter rejects or no provider matches — no terminate event is
        // emitted in that case. Without explicit cleanup the tree item would be stuck
        // showing the "Starting..." spinner forever.
        startDebuggingStub.resolves(false);

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /did not start the Aspire run session/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch reports error telemetry when startDebugging returns false', async () => {
        startDebuggingStub.resolves(false);
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /did not start the Aspire run session/);

            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.outcome, 'error');
            assert.strictEqual(event.properties?.error_kind, 'StartDebuggingDeclined');
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('launch cancels before starting debug session when CLI is unavailable', async () => {
        resolveCliPathStub.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), vscode.CancellationError);

            assert.strictEqual(startDebuggingStub.called, false);
            assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.outcome, 'canceled');
            assert.strictEqual(event.properties?.error_kind, undefined);
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            showErrorMessageStub.restore();
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('launch clears launching state and rethrows when startDebugging throws', async () => {
        startDebuggingStub.rejects(new Error('boom'));

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /boom/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch emits one bounded result telemetry event', async () => {
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await service.launch('/repo/AppHost.csproj', 'custom' as Parameters<AppHostLaunchService['launch']>[1], true);

            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.command, 'other');
            assert.strictEqual(event.properties?.outcome, 'success');
            assert.strictEqual(event.properties?.mode, 'run');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.execution_suppressed, 'false');
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('terminated run sessions include appHostPath and stop refresh semantics', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'run',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
            shouldMarkAppHostStopping: true,
        });
    });

    test('toolbar restart does not mark the replacement AppHost as stopping', () => {
        let terminationEvent: {
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });
        const sessionId = 'aspire-session';

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            id: sessionId,
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'run',
                __aspireAppHostRestartSourceSessionId: sessionId,
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
            shouldMarkAppHostStopping: false,
        });
    });

    test('terminating one of multiple equivalent run sessions does not mark the survivor as stopping', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.extname(String(filePath)) === '.csproj' ? 100n : 50n,
        }) as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });
        const workspaceRootSession = {
            id: 'upper',
            configuration: {
                type: 'aspire',
                program: '/workspace',
                command: 'run',
                [appHostTelemetryTargetPathConfigKey]: upperCasePath,
            },
        } as unknown as vscode.DebugSession;
        const lowerCaseSession = {
            id: 'lower',
            configuration: {
                type: 'aspire',
                program: lowerCasePath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;

        try {
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(workspaceRootSession);
            onDidStartDebugSessionCallback(lowerCaseSession);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(workspaceRootSession);
            onDidTerminateDebugSessionCallback(lowerCaseSession);

            assert.deepStrictEqual(terminationEvents, [
                {
                    appHostPath: upperCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: false,
                },
                {
                    appHostPath: lowerCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: true,
                },
            ]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('pending run is tracked before telemetry classification completes', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        try {
            await service.launch(upperCasePath, 'run', true);
            const upperCaseConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            const upperCaseSession = {
                id: 'upper',
                configuration: upperCaseConfiguration,
            } as unknown as vscode.DebugSession;
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(upperCaseSession);
            service.clearLaunching(upperCasePath);

            const lowerCaseLaunchTask = service.launch(lowerCasePath, 'run', true);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(upperCaseSession);
            await lowerCaseLaunchTask;

            assert.deepStrictEqual(terminationEvents, [{
                appHostPath: upperCasePath,
                command: 'run',
                shouldRequestStopRefresh: true,
                shouldMarkAppHostStopping: false,
            }]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('pending equivalent run prevents a terminated session from marking it as stopping', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        try {
            await service.launch(upperCasePath, 'run', true);
            const upperCaseConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            const upperCaseSession = {
                id: 'upper',
                configuration: upperCaseConfiguration,
            } as unknown as vscode.DebugSession;
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(upperCaseSession);
            service.clearLaunching(upperCasePath);

            await service.launch(lowerCasePath, 'run', true);
            const lowerCaseConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;
            const lowerCaseSession = {
                id: 'lower',
                configuration: lowerCaseConfiguration,
            } as unknown as vscode.DebugSession;
            service.clearLaunching(lowerCasePath);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(upperCaseSession);
            onDidStartDebugSessionCallback(lowerCaseSession);
            onDidTerminateDebugSessionCallback(lowerCaseSession);

            assert.deepStrictEqual(terminationEvents, [
                {
                    appHostPath: upperCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: false,
                },
                {
                    appHostPath: lowerCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: true,
                },
            ]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    function setEnvironmentVariable(name: string, value: string | undefined): void {
        if (value === undefined) {
            delete process.env[name];
        } else {
            process.env[name] = value;
        }
    }

    test('terminated non-run sessions do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'publish',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'publish',
            shouldRequestStopRefresh: false,
            shouldMarkAppHostStopping: false,
        });
    });

    test('terminated Aspire sessions default missing command to run and request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
            shouldMarkAppHostStopping: true,
        });
    });

    test('terminated Aspire sessions drop invalid command values and do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'invalid',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: undefined,
            shouldRequestStopRefresh: false,
            shouldMarkAppHostStopping: false,
        });
    });
});
