import * as assert from 'assert';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getAppHostInspectorAttachTimeoutMs, nodeDebuggerExtension } from '../debugger/languages/node';
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
        // js-debug's 10s default attach timeout expired on slow Windows runners and left Deno blocked
        // in --inspect-wait; the attach must be allowed the CLI's AppHost startup budget, which is the
        // 24h debug budget unless ASPIRE_CLI_START_TIMEOUT is configured.
        assert.strictEqual(debugConfig.timeout, getAppHostInspectorAttachTimeoutMs(undefined, process.env));
        assert.ok((debugConfig.timeout as number) > 10000);
    });

    test('waits for the Deno AppHost inspector as long as the launch configuration AppHost startup timeout', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();
        // The root launch configuration env reaches the CLI, not the AppHost child configuration.
        const debugSession = { configuration: { env: { ASPIRE_CLI_START_TIMEOUT: '300' } } } as unknown as AspireDebugSession;

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '/workspace/app/apphost.mts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession },
            debugConfig);

        assert.strictEqual(debugConfig.timeout, 300000);
    });

    test('computes the Deno AppHost inspector attach timeout from the CLI startup timeout', () => {
        // Debug sessions give the CLI a 24h startup budget unless one is configured.
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs(undefined, undefined, 'linux'), 86400000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({}, {}, 'linux'), 86400000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs(undefined, { ASPIRE_CLI_START_TIMEOUT: '' }, 'linux'), 86400000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs(undefined, { ASPIRE_CLI_START_TIMEOUT: '300' }, 'linux'), 300000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: ' 45 ' }, { ASPIRE_CLI_START_TIMEOUT: '300' }, 'linux'), 45000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: 600 }, undefined, 'linux'), 600000);
        // Values the CLI rejects (including an explicitly empty launch value) fall back to its default
        // rather than producing a zero/NaN timeout, which js-debug would treat as "never time out" or an immediate failure.
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: '' }, { ASPIRE_CLI_START_TIMEOUT: '300' }, 'linux'), 120000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: '0' }, undefined, 'linux'), 120000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: '-5' }, undefined, 'linux'), 120000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ ASPIRE_CLI_START_TIMEOUT: 'abc' }, undefined, 'linux'), 120000);
    });

    test('resolves the Deno AppHost inspector attach timeout case-insensitively on Windows only', () => {
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs(undefined, { aspire_cli_start_timeout: '300' }, 'win32'), 300000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs({ Aspire_Cli_Start_Timeout: '45' }, { ASPIRE_CLI_START_TIMEOUT: '300' }, 'win32'), 45000);
        assert.strictEqual(getAppHostInspectorAttachTimeoutMs(undefined, { aspire_cli_start_timeout: '300' }, 'linux'), 86400000);
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
