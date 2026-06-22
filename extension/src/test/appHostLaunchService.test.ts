import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostLaunchService } from '../services/AppHostLaunchService';

suite('AppHostLaunchService', () => {
    let service: AppHostLaunchService;
    let startDebuggingStub: sinon.SinonStub;
    let onDidTerminateDebugSessionStub: sinon.SinonStub;
    let onDidTerminateDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;

    setup(() => {
        onDidTerminateDebugSessionStub = sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            onDidTerminateDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        service = new AppHostLaunchService();
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
    });

    teardown(() => {
        service.dispose();
        startDebuggingStub.restore();
        onDidTerminateDebugSessionStub.restore();
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
    });

    test('launch includes step when doStep is provided', async () => {
        await service.launch('/repo/AppHost.csproj', 'do', true, 'deploy');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.command, 'do');
        assert.strictEqual(config.step, 'deploy');
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

    test('launch clears launching state and throws when startDebugging returns false', async () => {
        // vscode.debug.startDebugging returns Promise<boolean> and resolves false when
        // the debug adapter rejects or no provider matches — no terminate event is
        // emitted in that case. Without explicit cleanup the tree item would be stuck
        // showing the "Starting..." spinner forever.
        startDebuggingStub.resolves(false);

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /did not start the Aspire run session/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch failure event requests launch-failed notification for run command', async () => {
        const error = new Error('boom');
        startDebuggingStub.rejects(error);
        let failureEvent: unknown;
        service.onDidFailLaunch(event => {
            failureEvent = event;
        });

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /boom/);

        assert.deepStrictEqual(failureEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            noDebug: true,
            doStep: undefined,
            error,
            shouldRequestRefresh: true,
        });
    });

    test('launch failure event does not request launch-failed notification for non-run command', async () => {
        const error = new Error('boom');
        startDebuggingStub.rejects(error);
        let shouldRequestRefresh: boolean | undefined;
        service.onDidFailLaunch(event => {
            shouldRequestRefresh = event.shouldRequestRefresh;
        });

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'publish', true), /boom/);

        assert.strictEqual(shouldRequestRefresh, false);
    });

    test('launch clears launching state and rethrows when startDebugging throws', async () => {
        startDebuggingStub.rejects(new Error('boom'));

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /boom/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('terminated run sessions include appHostPath and stop refresh semantics', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
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
            launchFailed: false,
        });
    });

    test('terminated run session while still launching reports launchFailed', async () => {
        startDebuggingStub.resolves(true);
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; launchFailed: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        await service.launch('/repo/AppHost.csproj', 'run', true);
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);

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
            shouldRequestStopRefresh: false,
            launchFailed: true,
        });
    });

    test('terminated run session that already cleared launching reports launchFailed false', async () => {
        startDebuggingStub.resolves(true);
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; launchFailed: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        await service.launch('/repo/AppHost.csproj', 'run', true);
        service.clearLaunching('/repo/AppHost.csproj');
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);

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
            launchFailed: false,
        });
    });

    test('terminated non-run sessions do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
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
            launchFailed: false,
        });
    });

    test('terminated Aspire sessions default missing command to run and request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
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
            launchFailed: false,
        });
    });

    test('terminated Aspire sessions drop invalid command values and do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
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
            launchFailed: false,
        });
    });
});
