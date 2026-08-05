import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { nodeDebuggerExtension } from '../debugger/languages/node';
import { launchMethodDirect, launchMethodPackageManager } from '../debugger/languages/javascriptRuntime';
import { AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration } from '../dcp/types';

suite('Node Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    test('configures js-debug to capture process stdout and stderr', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            script_path: '/workspace/app/server.js',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.outputCapture, 'std');
        assert.strictEqual(debugConfig.cwd, '/workspace/app');
    });

    test('uses runtime arguments for package manager launches', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['run', 'dev'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.outputCapture, 'std');
        assert.strictEqual(debugConfig.runtimeExecutable, 'npm');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', 'dev']);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses a realistic package-manager launch with an explicit launch_method', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app',
            launch_method: launchMethodPackageManager
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['run', 'dev'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.runtimeExecutable, 'npm');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', 'dev']);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses a realistic direct launch with an explicit launch_method', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'node',
            script_path: '/workspace/app/server.js',
            working_directory: '/workspace/app',
            launch_method: launchMethodDirect
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.runtimeExecutable, 'node');
        assert.strictEqual(debugConfig.runtimeArgs, undefined);
        assert.strictEqual(debugConfig.program, '/workspace/app/server.js');
        assert.deepStrictEqual(debugConfig.args, []);
    });
});

suite('AspireDebugSession.startAppHost (node) Tests', () => {
    teardown(() => sinon.restore());

    function createSession(): AspireDebugSession {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/app/apphost.mts'
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession;

        return new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
    }

    test('launches the compiled AppHost output as program while keeping the source file as identity', async () => {
        const aspireDebugSession = createSession();
        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSessionStub = sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(undefined);

        await aspireDebugSession.startAppHost(
            '/workspace/app/apphost.mts',
            ['node', '/workspace/app/node_modules/.tmp/aspire-apphost/apphost.mjs'],
            [],
            true,
            { forceBuild: false });

        assert.ok(startAndGetDebugSessionStub.calledOnce);
        const debugConfig = startAndGetDebugSessionStub.firstCall.args[0] as AspireResourceExtendedDebugConfiguration;

        assert.strictEqual(debugConfig.program, '/workspace/app/node_modules/.tmp/aspire-apphost/apphost.mjs');
        assert.strictEqual(debugConfig.runtimeExecutable, 'node');
        assert.deepStrictEqual(debugConfig.args, []);
        assert.strictEqual(debugConfig.cwd, '/workspace/app');
    });
});

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'node',
        name: 'Node',
        request: 'launch',
        program: '/workspace/app/server.js',
        args: []
    };
}
