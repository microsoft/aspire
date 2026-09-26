import * as assert from 'assert';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { nodeDebuggerExtension } from '../debugger/languages/node';
import { launchMethodDirect, launchMethodPackageManager } from '../debugger/languages/javascriptRuntime';
import { AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration } from '../dcp/types';

suite('Node Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    test('advertises versioned Deno AppHost debugging support', () => {
        assert.ok(getSupportedCapabilities().includes('deno.v1'));
    });

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

    test('opens the Deno inspector when debugging an AppHost', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        const inspectorPort = debugConfig.attachSimplePort;
        assert.ok(typeof inspectorPort === 'number' && inspectorPort > 0);
        assert.strictEqual(debugConfig.runtimeExecutable, 'deno');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', `--inspect-wait=127.0.0.1:${inspectorPort}`, '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts']);
    });

    test('does not open the Deno inspector when running an AppHost without debugging', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts'],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts']);
        assert.strictEqual(debugConfig.attachSimplePort, undefined);
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
