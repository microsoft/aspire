import * as assert from 'assert';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { denoAppHostInspectorAttachTimeoutMs, hasCommandLineArgument, nodeDebuggerExtension } from '../debugger/languages/node';
import { launchMethodDirect, launchMethodPackageManager } from '../debugger/languages/javascriptRuntime';
import { AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration } from '../dcp/types';

suite('Node Debugger Tests', () => {
    const resourceCleanups: vscode.Disposable[] = [];
    const fakeAspireDebugSession = {
        registerResourceCleanup: (disposable: vscode.Disposable) => resourceCleanups.push(disposable)
    } as unknown as AspireDebugSession;

    teardown(() => {
        resourceCleanups.splice(0).forEach(disposable => disposable.dispose());
    });

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
        // js-debug's 10s default attach timeout expired on slow Windows runners and left Deno blocked
        // in --inspect-wait. The attach gets a bounded 120s budget that is independent of the CLI's
        // 24h debug startup budget.
        assert.strictEqual(debugConfig.timeout, denoAppHostInspectorAttachTimeoutMs);
        assert.strictEqual(denoAppHostInspectorAttachTimeoutMs, 120000);
    });

    test('preserves an explicitly configured debugger timeout for the Deno AppHost inspector attach', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = { ...createDebugConfig(), timeout: 300000 };

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '/workspace/app/apphost.mts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.timeout, 300000);
    });

    test('does not change the debugger timeout for non-Deno package manager launches', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', 'dev'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.timeout, undefined);
    });

    test('identifies the runtime waiting for the Deno AppHost inspector by its exact inspector argument', () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const linuxProcess = (args: string[]) => ({ pid: 2, parentPid: 1, executable: '/usr/bin/deno', command: args.join(' '), commandLineArguments: args });
        const windowsProcess = (command: string) => ({ pid: 2, parentPid: 1, executable: 'C:\\deno\\deno.exe', command });

        assert.strictEqual(hasCommandLineArgument(linuxProcess(['deno', 'run', argument, 'apphost.mts']), argument), true);
        assert.strictEqual(hasCommandLineArgument(linuxProcess(['deno', 'run', `${argument}5`, 'apphost.mts']), argument), false);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run ${argument} apphost.mts`), argument), true);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run "${argument}"`), argument), true);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run ${argument}5 apphost.mts`), argument), false);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run x${argument} apphost.mts`), argument), false);
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
        assert.strictEqual(debugConfig.timeout, undefined);
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
