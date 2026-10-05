import * as assert from 'assert';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { bunDebuggerExtension } from '../debugger/languages/bun';
import { launchMethodDirect, launchMethodPackageManager } from '../debugger/languages/javascriptRuntime';
import { AspireResourceExtendedDebugConfiguration, BunLaunchConfiguration } from '../dcp/types';

suite('Bun Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    async function configure(launchConfig: BunLaunchConfiguration, args: string[], debugConfig: AspireResourceExtendedDebugConfiguration): Promise<void> {
        await bunDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, args, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);
    }

    test('targets the bun adapter and maps runtime path to "runtime"', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/index.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await configure(launchConfig, ['/workspace/app/index.ts'], debugConfig);

        assert.strictEqual(debugConfig.type, 'bun');
        assert.strictEqual(debugConfig.cwd, '/workspace/app');
        assert.strictEqual(debugConfig.runtime, 'bun');
        assert.strictEqual(debugConfig.runtimeExecutable, undefined);
    });

    test('preserves required "program" in direct file mode and drops the duplicated script arg', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/index.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/index.ts');

        // DCP repeats the resolved script path as args[0]; the bun adapter would otherwise launch
        // `bun <program> <script>` with the script duplicated.
        await configure(launchConfig, ['/workspace/app/index.ts'], debugConfig);

        assert.strictEqual(debugConfig.program, '/workspace/app/index.ts');
        assert.deepStrictEqual(debugConfig.args, []);
    });

    test('keeps user-supplied args after the script in direct file mode', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/index.ts',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/index.ts');

        await configure(launchConfig, ['/workspace/app/index.ts', '--flag', 'value'], debugConfig);

        assert.strictEqual(debugConfig.program, '/workspace/app/index.ts');
        assert.deepStrictEqual(debugConfig.args, ['--flag', 'value']);
    });

    test('maps a package.json script launch onto "bun run <script>" and keeps "program" set', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/package.json',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig('/workspace/app/package.json');

        // .WithRunScript("start", ["--my-arg1"]) surfaces as ["run", "start", "--my-arg1"].
        await configure(launchConfig, ['run', 'start', '--my-arg1'], debugConfig);

        // program MUST stay set (the bun adapter throws "No program specified" otherwise), and the
        // spawned command must be `bun run start --my-arg1` with no trailing script-path argument.
        assert.strictEqual(debugConfig.program, 'run');
        assert.deepStrictEqual(debugConfig.args, ['start', '--my-arg1']);
        assert.strictEqual(debugConfig.runtime, 'bun');
    });

    test('honors an explicit "package-manager" launch_method over positional inference', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/index.ts',
            working_directory: '/workspace/app',
            launch_method: launchMethodPackageManager
        };
        const debugConfig = createDebugConfig('/workspace/app/index.ts');

        // args[0] is not "run", so legacy inference would pick direct mode; launch_method must win.
        await configure(launchConfig, ['./index.ts'], debugConfig);

        assert.strictEqual(debugConfig.program, './index.ts');
        assert.deepStrictEqual(debugConfig.args, []);
    });

    test('honors an explicit "direct" launch_method and preserves args when args[0] is not the script path', async () => {
        const launchConfig: BunLaunchConfiguration = {
            type: 'bun',
            runtime_executable: 'bun',
            script_path: '/workspace/app/index.ts',
            working_directory: '/workspace/app',
            launch_method: launchMethodDirect
        };
        const debugConfig = createDebugConfig('/workspace/app/index.ts');

        // args[0] === "run" would infer package-manager mode; launch_method forces direct mode. In
        // direct mode args[0] ("run") is NOT the script path, so the guarded slice keeps every arg.
        await configure(launchConfig, ['run', 'start'], debugConfig);

        assert.strictEqual(debugConfig.program, '/workspace/app/index.ts');
        assert.deepStrictEqual(debugConfig.args, ['run', 'start']);
    });
});

function createDebugConfig(program: string = '/workspace/app/index.ts'): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'bun',
        name: 'Bun',
        request: 'launch',
        program,
        args: []
    };
}
