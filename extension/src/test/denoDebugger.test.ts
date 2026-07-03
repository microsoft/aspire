import * as assert from 'assert';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { denoDebuggerExtension } from '../debugger/languages/deno';
import { AspireResourceExtendedDebugConfiguration, DenoLaunchConfiguration } from '../dcp/types';

suite('Deno Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    async function configure(launchConfig: DenoLaunchConfiguration, args: string[], debugConfig: AspireResourceExtendedDebugConfiguration): Promise<void> {
        await denoDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, args, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);
    }

    test('targets the built-in pwa-node adapter and forwards stdout/stderr', () => {
        assert.strictEqual(denoDebuggerExtension.resourceType, 'deno');
        assert.strictEqual(denoDebuggerExtension.debugAdapter, 'pwa-node');
        // Deno debugging needs no third-party debug adapter extension (uses js-debug).
        assert.strictEqual(denoDebuggerExtension.extensionId, null);
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
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', '--inspect-wait', '-A', 'main.ts']);
        // attachSimplePort pairs with --inspect-wait's default inspector port so js-debug attaches.
        assert.strictEqual(debugConfig.attachSimplePort, 9229);
        // The pwa-node simple-attach path drives the launch purely through runtimeExecutable + runtimeArgs.
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('injects --inspect-wait after the task sub-command for deno task launches', async () => {
        const launchConfig: DenoLaunchConfiguration = {
            type: 'deno',
            runtime_executable: 'deno',
            script_path: '/workspace/app/deno.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/deno.json');

        // .WithRunScript("dev") surfaces as ["task", "dev"].
        await configure(launchConfig, ['task', 'dev'], debugConfig);

        assert.deepStrictEqual(debugConfig.runtimeArgs, ['task', '--inspect-wait', 'dev']);
        assert.strictEqual(debugConfig.attachSimplePort, 9229);
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
