import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getSupportedCapabilities } from '../capabilities';
import { denoDebuggerExtension } from '../debugger/languages/deno';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration, DenoLaunchConfiguration } from '../dcp/types';

suite('Deno Debugger Tests', () => {
    const fakeAspireDebugSession = Object.create(AspireDebugSession.prototype) as AspireDebugSession;
    let registeredCleanupCount = 0;
    let resourceCleanups: vscode.Disposable[] = [];
    let terminateDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
    let terminateDebugSessionListenerDisposeCount = 0;

    fakeAspireDebugSession.registerResourceCleanup = cleanup => {
        registeredCleanupCount++;
        resourceCleanups.push(cleanup);
    };

    setup(() => {
        registeredCleanupCount = 0;
        resourceCleanups = [];
        terminateDebugSessionCallback = undefined;
        terminateDebugSessionListenerDisposeCount = 0;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateDebugSessionCallback = callback;
            return {
                dispose: () => terminateDebugSessionListenerDisposeCount++
            };
        });
    });

    teardown(() => {
        resourceCleanups.forEach(cleanup => cleanup.dispose());
        cleanupRun('1');
        sinon.restore();
    });

    async function configure(launchConfig: DenoLaunchConfiguration, args: string[], debugConfig: AspireResourceExtendedDebugConfiguration, debug: boolean = true): Promise<void> {
        await denoDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, args, [], { debug, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);
    }

    function assertInjectedInspectWaitArg(arg: string | undefined, debugConfig: AspireResourceExtendedDebugConfiguration): number {
        assert.ok(arg);
        const match = /^--inspect-wait=127\.0\.0\.1:(\d+)$/.exec(arg);
        assert.ok(match);

        const port = Number(match[1]);
        assert.ok(port > 0);
        assert.strictEqual(debugConfig.attachSimplePort, port);
        return port;
    }

    test('targets the built-in pwa-node adapter and forwards stdout/stderr', () => {
        assert.strictEqual(denoDebuggerExtension.resourceType, 'deno');
        assert.strictEqual(denoDebuggerExtension.debugAdapter, 'pwa-node');
        // Deno debugging needs no third-party debug adapter extension (uses js-debug).
        assert.strictEqual(denoDebuggerExtension.extensionId, null);
        assert.strictEqual(getSupportedCapabilities().filter(capability => capability === 'deno').length, 1);
    });

    test('runs TypeScript and JSX/TSX natively', () => {
        const fileTypes = denoDebuggerExtension.getSupportedFileTypes();
        assert.ok(fileTypes.includes('.ts'));
        assert.ok(fileTypes.includes('.tsx'));
        assert.ok(fileTypes.includes('.jsx'));
    });

    test('injects --inspect-wait after the run sub-command and drives js-debug via runtimeArgs', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        // Default AddDenoApp direct execution surfaces as ["run", "-A", "main.ts"].
        await configure(launchConfig, ['run', '-A', 'main.ts'], debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.outputCapture, 'std');
        assert.strictEqual(debugConfig.cwd, '/workspace/app');
        assert.strictEqual(debugConfig.runtimeExecutable, 'deno');
        // --inspect-wait must be inserted AFTER "run" (it is a runtime flag, not a script arg).
        assert.strictEqual(debugConfig.runtimeArgs?.[0], 'run');
        assertInjectedInspectWaitArg(debugConfig.runtimeArgs?.[1], debugConfig);
        assert.deepStrictEqual(debugConfig.runtimeArgs?.slice(2), ['-A', 'main.ts']);
        assert.strictEqual(registeredCleanupCount, 1);
        // The pwa-node simple-attach path drives the launch purely through runtimeExecutable + runtimeArgs.
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses a different inspector port for each injected debug session', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const firstDebugConfig = createDebugConfig('/workspace/app/main.ts');
        const secondDebugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '-A', 'main.ts'], firstDebugConfig);
        await configure(launchConfig, ['run', '-A', 'main.ts'], secondDebugConfig);

        const firstPort = assertInjectedInspectWaitArg(firstDebugConfig.runtimeArgs?.[1], firstDebugConfig);
        const secondPort = assertInjectedInspectWaitArg(secondDebugConfig.runtimeArgs?.[1], secondDebugConfig);
        assert.notStrictEqual(firstPort, secondPort);
        assert.strictEqual(registeredCleanupCount, 2);
    });

    test('does not release inspector port when a sibling debug session in the same run terminates', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '-A', 'main.ts'], debugConfig);

        assert.ok(terminateDebugSessionCallback);
        cleanupRun('1');

        assert.strictEqual(terminateDebugSessionListenerDisposeCount, 0);

        terminateDebugSessionCallback(createTerminatedDebugSession('other-debug-session', '2'));

        assert.strictEqual(terminateDebugSessionListenerDisposeCount, 0);

        terminateDebugSessionCallback(createTerminatedDebugSession('current-debug-session', '1'));

        assert.strictEqual(terminateDebugSessionListenerDisposeCount, 1);
    });

    test('rejects debug launches for deno task commands', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/deno.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/deno.json');

        // .WithRunScript("dev") surfaces as ["task", "dev"].
        await assert.rejects(
            () => configure(launchConfig, ['task', 'dev'], debugConfig),
            /Deno task launches cannot be debugged automatically/);

        assert.strictEqual(debugConfig.runtimeArgs, undefined);
        assert.strictEqual(debugConfig.attachSimplePort, undefined);
        assert.strictEqual(registeredCleanupCount, 0);
    });

    test('rejects explicit package-manager launch methods even when the argument shape is unfamiliar', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            launch_method: 'package-manager',
            runtime_executable: 'deno',
            script_path: '/workspace/app/deno.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/deno.json');

        await assert.rejects(
            () => configure(launchConfig, ['future-task-shape', 'dev'], debugConfig),
            /Deno task launches cannot be debugged automatically/);

        assert.strictEqual(debugConfig.runtimeArgs, undefined);
        assert.strictEqual(debugConfig.attachSimplePort, undefined);
        assert.strictEqual(registeredCleanupCount, 0);
    });

    test('uses legacy argument inference when launch_method is absent', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/deno.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/deno.json');

        await assert.rejects(
            () => configure(launchConfig, ['task', 'dev'], debugConfig),
            /Deno task launches cannot be debugged automatically/);

        assert.strictEqual(registeredCleanupCount, 0);
    });

    test('does not inject --inspect-wait for no-debug deno task launches', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/deno.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/deno.json');

        await configure(launchConfig, ['task', 'dev'], debugConfig, false);

        assert.deepStrictEqual(debugConfig.runtimeArgs, ['task', 'dev']);
        assert.strictEqual(debugConfig.attachSimplePort, undefined);
        assert.strictEqual(registeredCleanupCount, 0);
    });

    test('respects a user-configured inspector flag and does not double-inject', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        // WithDenoInspectBrk("127.0.0.1:9333") surfaces the flag already in the vector.
        await configure(launchConfig, ['run', '--inspect-brk=127.0.0.1:9333', '-A', 'main.ts'], debugConfig);

        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', '--inspect-brk=127.0.0.1:9333', '-A', 'main.ts']);
        // attachSimplePort is derived from the user-supplied inspector port.
        assert.strictEqual(debugConfig.attachSimplePort, 9333);
        assert.strictEqual(registeredCleanupCount, 0);
    });

    test('replaces an explicit inspector port of 0 with an allocated concrete port', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        // Deno reads ":0" as "choose an ephemeral port", so it listens on an unknown nonzero port.
        // Attaching to 0 would never connect; the flag must be rewritten to a concrete allocated port.
        await configure(launchConfig, ['run', '--inspect-wait=127.0.0.1:0', '-A', 'main.ts'], debugConfig);

        const injectedPort = assertInjectedInspectWaitArg(debugConfig.runtimeArgs?.[1], debugConfig);
        assert.notStrictEqual(injectedPort, 0);
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', `--inspect-wait=127.0.0.1:${injectedPort}`, '-A', 'main.ts']);
        assert.strictEqual(registeredCleanupCount, 1);
    });

    test('replaces a bare-host inspector port of 0 on --inspect-brk with an allocated concrete port', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '--inspect-brk=0', '-A', 'main.ts'], debugConfig);

        const injectedArg = debugConfig.runtimeArgs?.[1];
        const match = /^--inspect-brk=127\.0\.0\.1:(\d+)$/.exec(injectedArg ?? '');
        assert.ok(match, `expected an allocated --inspect-brk port, got ${injectedArg}`);
        const injectedPort = Number(match[1]);
        assert.notStrictEqual(injectedPort, 0);
        assert.strictEqual(debugConfig.attachSimplePort, injectedPort);
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', `--inspect-brk=127.0.0.1:${injectedPort}`, '-A', 'main.ts']);
        assert.strictEqual(registeredCleanupCount, 1);
    });

    test('ignores inspector-looking script arguments after the entrypoint', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '-A', 'main.ts', '--inspect=9229'], debugConfig);

        assert.strictEqual(debugConfig.runtimeArgs?.[0], 'run');
        const injectedPort = assertInjectedInspectWaitArg(debugConfig.runtimeArgs?.[1], debugConfig);
        assert.notStrictEqual(injectedPort, 9229);
        assert.deepStrictEqual(debugConfig.runtimeArgs?.slice(2), ['-A', 'main.ts', '--inspect=9229']);
        assert.strictEqual(registeredCleanupCount, 1);
    });

    test('allocates a different inspector port for each bare user-configured inspector flag', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const firstDebugConfig = createDebugConfig('/workspace/app/main.ts');
        const secondDebugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '--inspect-wait', '-A', 'main.ts'], firstDebugConfig);
        await configure(launchConfig, ['run', '--inspect-wait', '-A', 'main.ts'], secondDebugConfig);

        assert.strictEqual(firstDebugConfig.runtimeArgs?.[0], 'run');
        const firstPort = assertInjectedInspectWaitArg(firstDebugConfig.runtimeArgs?.[1], firstDebugConfig);
        assert.deepStrictEqual(firstDebugConfig.runtimeArgs?.slice(2), ['-A', 'main.ts']);

        assert.strictEqual(secondDebugConfig.runtimeArgs?.[0], 'run');
        const secondPort = assertInjectedInspectWaitArg(secondDebugConfig.runtimeArgs?.[1], secondDebugConfig);
        assert.deepStrictEqual(secondDebugConfig.runtimeArgs?.slice(2), ['-A', 'main.ts']);

        assert.notStrictEqual(firstPort, secondPort);
        assert.strictEqual(registeredCleanupCount, 2);
    });

    test('falls back to the deno executable when runtime_executable is absent', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            script_path: '/workspace/app/main.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/main.ts');

        await configure(launchConfig, ['run', '-A', 'main.ts'], debugConfig);

        assert.strictEqual(debugConfig.runtimeExecutable, 'deno');
        assertInjectedInspectWaitArg(debugConfig.runtimeArgs?.[1], debugConfig);
    });
});

function createDebugConfig(program: string = '/workspace/app/main.ts'): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'deno',
        name: 'Deno',
        request: 'launch',
        program,
        args: []
    };
}

function createTerminatedDebugSession(id: string, debugSessionId: string): vscode.DebugSession {
    return {
        id,
        type: 'deno',
        name: 'Deno',
        workspaceFolder: undefined,
        configuration: {
            type: 'deno',
            name: 'Deno',
            request: 'launch',
            runId: '1',
            debugSessionId
        },
        customRequest: async (_command: string, _args?: unknown) => undefined,
        getDebugProtocolBreakpoint: async (_breakpoint: vscode.Breakpoint) => undefined
    };
}
