import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import * as debuggerExtensionsModule from '../debugger/debuggerExtensions';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions, ResourceDebuggerExtension } from '../debugger/debuggerExtensions';
import { createRustDebuggerExtension, IRustService, RustService } from '../debugger/languages/rust';
import { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration, RustLaunchConfiguration } from '../dcp/types';
import { rustDebuggerExtensionNotInstalled } from '../loc/strings';
import {
    ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR,
    getAspireExtensionEnvironment,
} from '../utils/cliPathEnvironment';
import { extensionLogOutputChannel } from '../utils/logging';

type TestChildProcess = Omit<nodeChildProcess.ChildProcessWithoutNullStreams, 'exitCode' | 'signalCode'> & {
    exitCode: number | null;
    kill: sinon.SinonStub;
    signalCode: NodeJS.Signals | null;
    stderr: PassThrough;
    stdin: PassThrough;
    stdout: PassThrough;
};

class TestRustService implements IRustService {
    public buildStub: sinon.SinonStub;
    public getCargoHostTargetStub = sinon.stub().resolves(undefined);

    constructor(error?: Error) {
        this.buildStub = sinon.stub();
        if (error) {
            this.buildStub.rejects(error);
        } else {
            this.buildStub.callsFake((
                _workingDirectory: string,
                _cargoArgs: string[],
                _env: EnvVar[],
                executablePath: string | undefined
            ) => Promise.resolve(executablePath ?? '/workspace/api/target/debug/api'));
        }
    }

    build(
        workingDirectory: string,
        cargoArgs: string[],
        env: EnvVar[],
        executablePath: string | undefined
    ): Promise<string> {
        return this.buildStub(workingDirectory, cargoArgs, env, executablePath);
    }

    getCargoHostTarget(
        workingDirectory: string,
        env: EnvVar[]
    ): Promise<string | undefined> {
        return this.getCargoHostTargetStub(workingDirectory, env);
    }
}

suite('Rust Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    const rustExtensionId = process.platform === 'win32' ? 'ms-vscode.cpptools' : 'vadimcn.vscode-lldb';
    const rustDebugAdapter = process.platform === 'win32' ? 'cppvsdbg' : 'lldb';

    teardown(() => sinon.restore());

    function createExtension(
        error?: Error,
        platform?: NodeJS.Platform,
        installedExtensions: string[] = []
    ): { rustService: TestRustService, extension: ResourceDebuggerExtension } {
        const rustService = new TestRustService(error);
        return {
            rustService,
            extension: createRustDebuggerExtension(
                () => rustService,
                platform,
                extensionId => installedExtensions.includes(extensionId))
        };
    }

    test('advertises Rust support when the platform-specific debugger extension is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            return extensionId === rustExtensionId ? { id: extensionId } as vscode.Extension<unknown> : undefined;
        });

        const capabilities = getSupportedCapabilities();
        assert.ok(capabilities.includes('rust'));
        assert.ok(capabilities.includes(rustExtensionId));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'rust'));
    });

    test('does not advertise Rust support when the platform-specific debugger extension is missing', () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

        const capabilities = getSupportedCapabilities();
        assert.ok(!capabilities.includes('rust'));
        assert.ok(!getResourceDebuggerExtensions().some(extension => extension.resourceType === 'rust'));
    });

    test('advertises and selects CodeLLDB when it is the only Windows Rust adapter installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            return extensionId === 'vadimcn.vscode-lldb' ? { id: extensionId } as vscode.Extension<unknown> : undefined;
        });

        const capabilities = getSupportedCapabilities('win32');
        assert.ok(capabilities.includes('rust'));
        assert.ok(capabilities.includes('vadimcn.vscode-lldb'));
        assert.ok(!capabilities.includes('ms-vscode.cpptools'));

        const { extension } = createExtension(undefined, 'win32', ['vadimcn.vscode-lldb']);
        assert.strictEqual(extension.extensionId, 'vadimcn.vscode-lldb');
        assert.strictEqual(extension.debugAdapter, 'lldb');
    });

    test('refreshes the Windows Rust adapter when installed extensions change', () => {
        let installedExtensions = new Set(['ms-vscode.cpptools']);
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            return installedExtensions.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined;
        });

        const initialExtension = getResourceDebuggerExtensions('win32')
            .find(extension => extension.resourceType === 'rust');
        assert.strictEqual(initialExtension?.extensionId, 'ms-vscode.cpptools');
        assert.strictEqual(initialExtension?.debugAdapter, 'cppvsdbg');

        installedExtensions = new Set(['vadimcn.vscode-lldb']);
        const refreshedExtension = getResourceDebuggerExtensions('win32')
            .find(extension => extension.resourceType === 'rust');
        assert.strictEqual(refreshedExtension?.extensionId, 'vadimcn.vscode-lldb');
        assert.strictEqual(refreshedExtension?.debugAdapter, 'lldb');
    });

    test('stops a Rust AppHost launch with install guidance when no debugger extension is installed', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration');
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.rs',
                command: 'run',
            },
        };
        const debugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');

        await debugSession.startAppHost('/workspace/apphost.rs', ['cargo', 'run'], [], true, { forceBuild: false });

        sinon.assert.notCalled(createDebugSessionConfiguration);
        sinon.assert.calledOnce(showErrorMessage);
        assert.strictEqual(showErrorMessage.firstCall.args[0], rustDebuggerExtensionNotInstalled(rustExtensionId));
    });

    test('builds the crate and debugs the executable the app host resolved', async () => {
        const { rustService, extension } = createExtension();
        const debugConfig = createDebugConfig();

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--release'], '/workspace/api/target/release/api'),
            ['--listen', ':8080'],
            [{ name: 'RUSTFLAGS', value: '-C target-cpu=native' }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        // The resource environment has to reach the build: it carries settings such as RUSTFLAGS and
        // CARGO_* that change what cargo produces.
        assert.ok(rustService.buildStub.calledWith(
            '/workspace/api',
            ['build', '--release'],
            [{ name: 'RUSTFLAGS', value: '-C target-cpu=native' }]));
        assert.strictEqual(debugConfig.program, '/workspace/api/target/release/api');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
        assert.deepStrictEqual(debugConfig.args, ['--listen', ':8080']);
        if (process.platform === 'win32') {
            assert.ok(rustService.getCargoHostTargetStub.calledOnceWith(
                '/workspace/api',
                [{ name: 'RUSTFLAGS', value: '-C target-cpu=native' }]));
        } else {
            assert.ok(rustService.getCargoHostTargetStub.notCalled);
        }

        if (rustDebugAdapter === 'cppvsdbg') {
            assert.strictEqual(debugConfig.console, 'internalConsole');
            assert.ok(Array.isArray(debugConfig.environment));
        } else {
            assert.deepStrictEqual(debugConfig.sourceLanguages, ['rust']);
        }
    });

    test('passes cargo target selection arguments through to the build', async () => {
        const { rustService, extension } = createExtension();
        const debugConfig = createDebugConfig();

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--bin', 'worker'], '/workspace/api/target/debug/worker'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.buildStub.calledWith('/workspace/api', ['build', '--bin', 'worker'], []));
        assert.strictEqual(debugConfig.program, '/workspace/api/target/debug/worker');
    });

    test('does not ask cargo for build messages because the executable is already known', async () => {
        const { rustService, extension } = createExtension();

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/debug/api'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            createDebugConfig());

        const cargoArgs = rustService.buildStub.firstCall.args[1] as string[];
        assert.deepStrictEqual(cargoArgs, ['build']);
    });

    test('propagates build failures instead of starting a debug session', async () => {
        const { extension } = createExtension(new Error('cargo build failed in /workspace/api with exit code 101.'));

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], '/workspace/api/target/debug/api'),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()),
            /cargo build failed/);
    });

    test('reports a non-zero cargo exit from the spawned process', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from('error: compilation failed\n'));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!(error instanceof vscode.CancellationError));
            assert.ok(error.message.includes('exit code 101'));
            assert.ok(error.message.includes('error: compilation failed'));
            return true;
        });
    });

    test('reports user disposal during cargo build as cancellation', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');

        harness.disposeSession();
        assert.ok(harness.childProcess.kill.calledOnce);
        harness.childProcess.emit('close', null, 'SIGTERM');

        await assert.rejects(build, error => error instanceof vscode.CancellationError);
    });

    test('probes the Cargo host with the build environment', async () => {
        const harness = createRustProcessHarness();
        const env = [{ name: 'RUSTFLAGS', value: '-C target-cpu=native' }];
        const probe = harness.rustService.getCargoHostTarget('/workspace/api', env);

        harness.childProcess.stdout.write([
            'cargo 1.89.0 (c24e10642 2025-06-23)',
            'release: 1.89.0',
            'host: x86_64-pc-windows-msvc',
            '',
        ].join('\n'));
        harness.childProcess.emit('close', 0, null);

        assert.strictEqual(await probe, 'x86_64-pc-windows-msvc');
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.strictEqual(spawn.firstCall.args[0], 'cargo');
        assert.deepStrictEqual(spawn.firstCall.args[1], ['-Vv']);
        assert.strictEqual(spawn.firstCall.args[2].cwd, '/workspace/api');
        assert.strictEqual(spawn.firstCall.args[2].env.RUSTFLAGS, '-C target-cpu=native');
    });

    test('reports user disposal during the Cargo host probe as cancellation', async () => {
        const harness = createRustProcessHarness();
        const probe = harness.rustService.getCargoHostTarget('/workspace/api', []);

        harness.disposeSession();
        assert.ok(harness.childProcess.kill.calledOnce);
        harness.childProcess.emit('close', null, 'SIGTERM');

        await assert.rejects(probe, error => error instanceof vscode.CancellationError);
    });

    test('forcefully terminates a Cargo host probe that ignores session cancellation', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const harness = createRustProcessHarness();

        try {
            const probe = harness.rustService.getCargoHostTarget('/workspace/api', []);
            const cancellation = assert.rejects(probe, error => error instanceof vscode.CancellationError);

            harness.disposeSession();

            assert.deepStrictEqual(harness.childProcess.kill.firstCall.args, [undefined]);
            await cancellation;

            await clock.tickAsync(5_000);

            assert.strictEqual(harness.childProcess.kill.callCount, 2);
            assert.deepStrictEqual(harness.childProcess.kill.secondCall.args, ['SIGKILL']);
            assert.strictEqual(clock.countTimers(), 0);

            harness.childProcess.emit('close', null, 'SIGKILL');
            assert.strictEqual(harness.childProcess.listenerCount('close'), 0);
            assert.strictEqual(harness.childProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);
        } finally {
            harness.childProcess.emit('close', null, 'SIGKILL');
            clock.restore();
        }
    });

    test('does not forcefully terminate a Cargo host probe that closes during the cancellation grace period', async () => {
        const clock = sinon.useFakeTimers({
            shouldClearNativeTimers: true,
            toFake: ['setTimeout', 'clearTimeout'],
        });
        const harness = createRustProcessHarness();

        try {
            const probe = harness.rustService.getCargoHostTarget('/workspace/api', []);
            const cancellation = assert.rejects(probe, error => error instanceof vscode.CancellationError);

            harness.disposeSession();

            assert.deepStrictEqual(harness.childProcess.kill.firstCall.args, [undefined]);
            assert.strictEqual(clock.countTimers(), 1);

            harness.childProcess.emit('close', null, 'SIGTERM');
            await cancellation;
            await clock.tickAsync(0);

            assert.strictEqual(harness.childProcess.listenerCount('close'), 0);
            assert.strictEqual(harness.childProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);

            await clock.tickAsync(5_000);

            assert.strictEqual(harness.childProcess.kill.callCount, 1);
            assert.strictEqual(clock.countTimers(), 0);
        } finally {
            harness.childProcess.emit('close', null, 'SIGTERM');
            clock.restore();
        }
    });

    test('times out a Cargo host probe that never closes', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const warning = sinon.stub(extensionLogOutputChannel, 'warn');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const secret = 'private-cargo-timeout-credential';
        let result: string | undefined | 'pending' = 'pending';

        try {
            void harness.rustService.getCargoHostTarget(
                '/workspace/api',
                [{ name: 'PRIVATE_REGISTRY_TOKEN', value: secret }])
                .then(value => result = value);

            await clock.tickAsync(5_000);

            assert.strictEqual(result, undefined);
            assert.ok(harness.childProcess.kill.calledOnceWithExactly('SIGKILL'));
            assert.strictEqual(harness.childProcess.listenerCount('close'), 1);
            assert.strictEqual(harness.childProcess.listenerCount('error'), 1);
            assert.strictEqual(harness.childProcess.stdout.listenerCount('data'), 0);
            assert.strictEqual(clock.countTimers(), 0);

            const persistentLogs = [...info.getCalls(), ...warning.getCalls(), ...errorLog.getCalls()]
                .map(call => String(call.args[0]));
            assert.ok(persistentLogs.some(message => message.includes('Cargo host target probe timed out')));
            assert.ok(persistentLogs.every(message => !message.includes(secret)));
            assert.ok(persistentLogs.every(message => !message.includes('launch_failed')));

            harness.childProcess.emit('close', null, 'SIGKILL');
            assert.strictEqual(harness.childProcess.listenerCount('close'), 0);
            assert.strictEqual(harness.childProcess.listenerCount('error'), 0);
        } finally {
            harness.childProcess.emit('close', null, 'SIGKILL');
            clock.restore();
        }
    });

    test('sinks delayed Cargo host probe kill errors until close', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const warning = sinon.stub(extensionLogOutputChannel, 'warn');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const secret = 'private-delayed-kill-error';

        try {
            const probe = harness.rustService.getCargoHostTarget('/workspace/api', []);

            await clock.tickAsync(5_000);

            assert.strictEqual(await probe, undefined);
            const delayedKillError = new Error(secret);
            delayedKillError.name = secret;
            assert.doesNotThrow(() => harness.childProcess.emit('error', delayedKillError));

            const persistentLogs = [...info.getCalls(), ...warning.getCalls(), ...errorLog.getCalls()]
                .map(call => String(call.args[0]));
            assert.ok(persistentLogs.every(message => !message.includes(secret)));

            harness.childProcess.emit('close', null, 'SIGKILL');
            assert.strictEqual(harness.childProcess.listenerCount('close'), 0);
            assert.strictEqual(harness.childProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);
        } finally {
            harness.childProcess.emit('close', null, 'SIGKILL');
            clock.restore();
        }
    });

    test('does not expose Cargo host probe failure output', async () => {
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const secret = 'private-cargo-probe-credential';
        const probe = harness.rustService.getCargoHostTarget(
            '/workspace/api',
            [{ name: 'PRIVATE_REGISTRY_TOKEN', value: secret }]);

        harness.childProcess.stderr.write(`error: registry rejected ${secret}\n`);
        harness.childProcess.emit('close', 101, null);

        assert.strictEqual(await probe, undefined);
        const persistentLogs = [...info.getCalls(), ...errorLog.getCalls()].map(call => String(call.args[0]));
        assert.ok(persistentLogs.some(message => message.includes('Cargo host target probe')));
        assert.ok(persistentLogs.every(message => !message.includes(secret)));
    });

    test('does not expose Cargo host probe spawn errors', async () => {
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const secret = 'private-cargo-spawn-error';
        const probe = harness.rustService.getCargoHostTarget('/workspace/api', []);
        const spawnError = new Error(secret);
        spawnError.name = secret;

        harness.childProcess.emit('error', spawnError);

        assert.strictEqual(await probe, undefined);
        const persistentLogs = [...info.getCalls(), ...errorLog.getCalls()].map(call => String(call.args[0]));
        assert.ok(persistentLogs.some(message => message.includes('failed to start')));
        assert.ok(persistentLogs.every(message => !message.includes(secret)));
    });

    test('does not expose synchronous Cargo host probe spawn failures', async () => {
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const secret = 'private-synchronous-cargo-spawn-error';
        const spawnError = new Error(secret);
        spawnError.name = secret;
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        spawn.throws(spawnError);

        assert.strictEqual(
            await harness.rustService.getCargoHostTarget('/workspace/api', []),
            undefined);
        const persistentLogs = [...info.getCalls(), ...errorLog.getCalls()].map(call => String(call.args[0]));
        assert.ok(persistentLogs.some(message => message.includes('failed to start')));
        assert.ok(persistentLogs.every(message => !message.includes(secret)));
    });

    test('does not convert an exited cargo build to cancellation while streams are closing', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');

        harness.childProcess.exitCode = 101;
        harness.disposeSession();
        harness.childProcess.emit('close', 101, null);

        assert.ok(harness.childProcess.kill.notCalled);
        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!(error instanceof vscode.CancellationError));
            assert.ok(error.message.includes('exit code 101'));
            return true;
        });
    });

    test('reports an unrequested cargo SIGTERM as a build failure', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');

        harness.childProcess.emit('close', null, 'SIGTERM');

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!(error instanceof vscode.CancellationError));
            assert.ok(error.message.includes('SIGTERM'));
            return true;
        });
    });

    test('reconstructs UTF-8 output split across process chunks', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');
        const stderr = 'cargo: naïve 🦀\n';
        const bytes = Buffer.from(stderr);
        const crabStart = bytes.indexOf(Buffer.from('🦀'));

        harness.childProcess.stderr.write(bytes.subarray(0, crabStart + 2));
        harness.childProcess.stderr.write(bytes.subarray(crabStart + 2));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => error instanceof Error && error.message.includes(stderr));
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);
    });

    test('retains only a marked UTF-8 tail of large cargo stderr', async () => {
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const errorLog = sinon.stub(extensionLogOutputChannel, 'error');
        const build = harness.rustService.build('/workspace/api', ['build'], [], '/workspace/api/target/debug/api');
        const secret = 'persistent-secret-value';
        const stderr = `${secret}\n${'x'.repeat(20_000)}\nuseful tail 🦀`;

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(error.message.includes('[cargo stderr truncated to the last 8192 characters.]'));
            assert.ok(error.message.endsWith('useful tail 🦀'));
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.length < 9_000);
            return true;
        });
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);

        const persistentLogs = [...info.getCalls(), ...errorLog.getCalls()].map(call => String(call.args[0]));
        assert.ok(persistentLogs.every(message => !message.includes(secret)));
    });

    test('redacts resource environment values from retained cargo errors', async () => {
        const harness = createRustProcessHarness();
        const secret = 'launch-stderr-secret-value';
        const stderr = `error: private registry rejected ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build'],
            [{ name: 'PRIVATE_REGISTRY_TOKEN', value: secret }],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);
    });

    test('redacts credential-bearing URL resource environment values from retained cargo errors', async () => {
        const harness = createRustProcessHarness();
        const secret = 'postgresql://user:password@database.internal/app';
        const stderr = `error: database connection failed for ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build'],
            [{ name: 'DATABASE_URL', value: secret }],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);
    });

    test('preserves ordinary environment values in retained cargo diagnostics', async () => {
        const harness = createRustProcessHarness();
        const stderr = 'debug assertion failed at src/main.rs:12 due to 1 previous error\n';
        const build = harness.rustService.build(
            '/workspace/api',
            ['build'],
            [
                { name: 'RUST_LOG', value: 'debug' },
                { name: 'RUST_BACKTRACE', value: '1' },
            ],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => error instanceof Error && error.message.includes(stderr));
    });

    test('redacts sensitive Cargo argument values from retained errors', async () => {
        const harness = createRustProcessHarness();
        const secret = 'private-registry-credential';
        const stderr = `error: invalid registry token ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', `registries.private.token=${secret}`],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);
    });

    test('redacts sensitive inline Cargo config values from retained errors', async () => {
        const harness = createRustProcessHarness();
        const secret = 'inline-private-registry-credential';
        const stderr = `error: invalid registry token ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', `--config=registries.private.token=${secret}`],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
    });

    test('redacts quoted Cargo config secrets with TOML whitespace', async () => {
        const harness = createRustProcessHarness();
        const secret = 'quoted-private-registry-credential';
        const stderr = `error: invalid registry token ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', `registries.private.token = "${secret}"`],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
    });

    test('redacts conventional secret environment names in Cargo config', async () => {
        const harness = createRustProcessHarness();
        const secret = 'cargo-config-postgres-credential';
        const stderr = `error: invalid environment value ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', `env.PGPASSWORD="${secret}"`],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
    });

    test('redacts secrets nested in Cargo config inline tables', async () => {
        const harness = createRustProcessHarness();
        const secret = 'cargo-config-inline-table-credential';
        const stderr = `error: invalid environment value ${secret}\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', `env.PGPASSWORD={ value = "${secret}", force = true }`],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
    });

    test('redacts secrets from rejected top-level Cargo config inline tables', async () => {
        const harness = createRustProcessHarness();
        const secret = 'cargo-config-top-level-inline-table-credential';
        const configuration = `env = { "PGPASSWORD" = "${secret}" }`;
        const stderr = `error: --config argument \`${configuration}\` was not a TOML dotted key expression\n`;
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', configuration],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.stderr.write(Buffer.from(stderr));
        harness.childProcess.emit('close', 101, null);

        await assert.rejects(build, error => {
            assert.ok(error instanceof Error);
            assert.ok(!error.message.includes(secret));
            assert.ok(error.message.includes('[redacted]'));
            return true;
        });
        assert.strictEqual(harness.getDebugOutput('stderr'), stderr);
    });

    test('redacts inherited sensitive environment values from retained errors', async () => {
        const harness = createRustProcessHarness();
        const environmentName = 'PGPASSWORD';
        const originalValue = process.env[environmentName];
        const secret = 'inherited-private-registry-credential';
        process.env[environmentName] = secret;

        try {
            const build = harness.rustService.build(
                '/workspace/api',
                ['build'],
                [],
                '/workspace/api/target/debug/api');
            harness.childProcess.stderr.write(Buffer.from(`error: inherited token ${secret}\n`));
            harness.childProcess.emit('close', 101, null);

            await assert.rejects(build, error => {
                assert.ok(error instanceof Error);
                assert.ok(!error.message.includes(secret));
                assert.ok(error.message.includes('[redacted]'));
                return true;
            });
        } finally {
            if (originalValue === undefined) {
                delete process.env[environmentName];
            } else {
                process.env[environmentName] = originalValue;
            }
        }
    });

    test('discovers the executable for older Rust launch metadata', async () => {
        const { rustService, extension } = createExtension();
        rustService.buildStub.resolves('/workspace/api/target/debug/api');
        const debugConfig = createDebugConfig();

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], undefined),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.deepStrictEqual(rustService.buildStub.firstCall.args, [
            '/workspace/api',
            ['build'],
            [],
            undefined,
        ]);
        assert.strictEqual(debugConfig.program, '/workspace/api/target/debug/api');
    });

    test('discovers a legacy executable through the spawned Cargo artifact stream', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build('/workspace/api', ['build'], [], undefined);

        completeLegacyCargoBuild(harness);

        assert.strictEqual(await build, '/workspace/api/target/debug/api');
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.deepStrictEqual(spawn.firstCall.args[1], ['build', '--message-format=json']);
    });

    test('adds the legacy JSON message format before rustc arguments', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build(
            '/workspace/api',
            ['rustc', '--bin', 'api', '--', '-C', 'link-arg=--message-format=human'],
            [],
            undefined);

        completeLegacyCargoBuild(harness);

        await build;
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.deepStrictEqual(spawn.firstCall.args[1], [
            'rustc',
            '--bin',
            'api',
            '--message-format=json',
            '--',
            '-C',
            'link-arg=--message-format=human',
        ]);
    });

    test('replaces a split legacy Cargo message format', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--message-format', 'short', '--release'],
            [],
            undefined);

        completeLegacyCargoBuild(harness);

        await build;
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.deepStrictEqual(spawn.firstCall.args[1], ['build', '--message-format=json', '--release']);
    });

    test('replaces equals-form legacy Cargo message formats deterministically', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--message-format=short', '--release', '--message-format', 'human'],
            [],
            undefined);

        completeLegacyCargoBuild(harness);

        await build;
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.deepStrictEqual(spawn.firstCall.args[1], ['build', '--message-format=json', '--release']);
    });

    test('preserves message-format-like arguments after the Cargo separator', async () => {
        const harness = createRustProcessHarness();
        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--release', '--', '--message-format', 'human'],
            [],
            undefined);

        completeLegacyCargoBuild(harness);

        await build;
        const spawn = nodeChildProcess.spawn as unknown as sinon.SinonStub;
        assert.deepStrictEqual(spawn.firstCall.args[1], [
            'build',
            '--release',
            '--message-format=json',
            '--',
            '--message-format',
            'human',
        ]);
    });

    test('uses cppvsdbg for an MSVC Rust target on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--target', 'x86_64-pc-windows-msvc'], '/workspace/api/target/x86_64-pc-windows-msvc/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(extension.extensionId, 'ms-vscode.cpptools');
        assert.strictEqual(extension.debugAdapter, 'cppvsdbg');
        assert.strictEqual(debugConfig.type, 'cppvsdbg');
        assert.strictEqual(debugConfig.console, 'internalConsole');
        assert.ok(Array.isArray(debugConfig.environment));
        assert.ok(rustService.buildStub.calledOnce);
        assert.ok(rustService.getCargoHostTargetStub.notCalled);
    });

    test('materializes the authoritative AppHost identity for the Rust cppvsdbg adapter', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const { extension } = createExtension(undefined, 'win32');
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.18.0',
            preRelease: true,
        }, {
            appName: 'Visual Studio Code - Insiders',
            uriScheme: 'vscode-insiders',
        });
        assert.ok(extensionEnvironment);
        const debuggerEnvironment = {
            aspire_vscode_extension_version: 'debugger-version',
            aspire_vscode_extension_channel: 'stable',
            aspire_vscode_extension_source: 'other',
            CALLER_SETTING: 'preserved',
        };

        try {
            const configuration = await createDebugSessionConfiguration(
                {
                    type: 'aspire',
                    request: 'launch',
                    name: 'Aspire',
                    program: '/workspace/apphost.rs',
                    debuggers: {
                        apphost: {
                            env: debuggerEnvironment,
                        },
                    },
                },
                createLaunchConfig(
                    ['build', '--target', 'x86_64-pc-windows-msvc'],
                    '/workspace/api/target/x86_64-pc-windows-msvc/debug/api.exe'),
                [],
                [
                    { name: 'aspire_vscode_extension_version', value: 'caller-version' },
                    { name: 'aspire_vscode_extension_channel', value: 'stable' },
                    { name: 'aspire_vscode_extension_source', value: 'other' },
                    { name: 'CALLER_SETTING', value: 'preserved' },
                ],
                {
                    debug: true,
                    runId: 'apphost-run',
                    debugSessionId: 'aspire-session',
                    isApphost: true,
                    debugSession: { aspireExtensionEnvironment: extensionEnvironment } as AspireDebugSession,
                },
                extension);

            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR], '1.18.0');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR], 'prerelease');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'microsoft-marketplace');
            assert.strictEqual(configuration.env?.aspire_vscode_extension_version, undefined);
            assert.strictEqual(configuration.env?.aspire_vscode_extension_channel, undefined);
            assert.strictEqual(configuration.env?.aspire_vscode_extension_source, undefined);

            const adapterEnvironment = Object.fromEntries(
                (configuration.environment as { name: string; value: string }[])
                    .map(({ name, value }) => [name, value]));
            assert.deepStrictEqual({
                [ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR]: adapterEnvironment[ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR],
                [ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR]: adapterEnvironment[ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR],
                [ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR]: adapterEnvironment[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR],
                aspire_vscode_extension_version: adapterEnvironment.aspire_vscode_extension_version,
                aspire_vscode_extension_channel: adapterEnvironment.aspire_vscode_extension_channel,
                aspire_vscode_extension_source: adapterEnvironment.aspire_vscode_extension_source,
                CALLER_SETTING: adapterEnvironment.CALLER_SETTING,
            }, {
                [ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR]: '1.18.0',
                [ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR]: 'prerelease',
                [ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR]: 'microsoft-marketplace',
                aspire_vscode_extension_version: undefined,
                aspire_vscode_extension_channel: undefined,
                aspire_vscode_extension_source: undefined,
                CALLER_SETTING: 'preserved',
            });
            assert.deepStrictEqual(debuggerEnvironment, {
                aspire_vscode_extension_version: 'debugger-version',
                aspire_vscode_extension_channel: 'stable',
                aspire_vscode_extension_source: 'other',
                CALLER_SETTING: 'preserved',
            });
        }
        finally {
            platformStub.restore();
        }
    });

    test('does not probe when the executable path contains an MSVC target triple', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/x86_64-pc-windows-msvc/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.getCargoHostTargetStub.notCalled);
        assert.ok(rustService.buildStub.calledOnce);
        assert.strictEqual(debugConfig.type, 'cppvsdbg');
    });

    test('uses cppvsdbg for the default MSVC Cargo host on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        rustService.getCargoHostTargetStub.resolves('x86_64-pc-windows-msvc');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;
        const env = [{ name: 'RUSTFLAGS', value: '-C target-cpu=native' }];

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/debug/api.exe'),
            [],
            env,
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.getCargoHostTargetStub.calledOnceWith('/workspace/api', env));
        assert.ok(rustService.buildStub.calledOnce);
        assert.strictEqual(debugConfig.type, 'cppvsdbg');
    });

    test('rejects the default GNU Cargo host on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        rustService.getCargoHostTargetStub.resolves('x86_64-pc-windows-gnu');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], '/workspace/api/target/debug/api.exe'),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            /cannot debug Rust target.+CodeLLDB.+windows-msvc/);

        assert.ok(rustService.getCargoHostTargetStub.calledOnce);
        assert.ok(rustService.buildStub.notCalled);
    });

    test('propagates cancellation from the Cargo host probe on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        rustService.getCargoHostTargetStub.rejects(new vscode.CancellationError());
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], '/workspace/api/target/debug/api.exe'),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            error => error instanceof vscode.CancellationError);

        assert.ok(rustService.getCargoHostTargetStub.calledOnce);
        assert.ok(rustService.buildStub.notCalled);
    });

    test('continues when the Cargo host probe cannot determine a target on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.getCargoHostTargetStub.calledOnce);
        assert.ok(rustService.buildStub.calledOnce);
        assert.strictEqual(debugConfig.type, 'cppvsdbg');
    });

    test('does not probe the Cargo host when CodeLLDB is selected on Windows', async () => {
        const { rustService, extension } = createExtension(
            undefined,
            'win32',
            ['vadimcn.vscode-lldb']);
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.getCargoHostTargetStub.notCalled);
        assert.ok(rustService.buildStub.calledOnce);
        assert.strictEqual(debugConfig.type, 'lldb');
    });

    test('preserves a user-configured Rust debug adapter', async () => {
        const { extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = 'cppdbg';

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--target', 'x86_64-pc-windows-msvc'], '/workspace/api/target/x86_64-pc-windows-msvc/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'cppdbg');
        assert.ok(Array.isArray(debugConfig.environment));
    });

    test('uses a user-configured GNU-compatible adapter without requiring CodeLLDB', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = 'cppdbg';

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--target=x86_64-pc-windows-gnu'], '/workspace/api/target/x86_64-pc-windows-gnu/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'cppdbg');
        assert.ok(rustService.buildStub.calledOnce);
    });

    test('uses CodeLLDB for a GNU Rust target on Windows when it is installed', async () => {
        const { rustService, extension } = createExtension(
            undefined,
            'win32',
            ['ms-vscode.cpptools', 'vadimcn.vscode-lldb']);
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        assert.strictEqual(extension.extensionId, 'ms-vscode.cpptools');
        assert.strictEqual(extension.debugAdapter, 'cppvsdbg');

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--target=x86_64-pc-windows-gnu'], '/workspace/api/target/x86_64-pc-windows-gnu/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'lldb');
        assert.deepStrictEqual(debugConfig.sourceLanguages, ['rust']);
        assert.strictEqual(debugConfig.environment, undefined);
        assert.ok(rustService.buildStub.calledOnce);
    });

    test('rejects a GNU Rust target on Windows when CodeLLDB is missing', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], '/workspace/api/target/x86_64-pc-windows-gnu/debug/api.exe'),
                [],
                [{ name: 'CARGO_BUILD_TARGET', value: 'x86_64-pc-windows-gnu' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()),
            /cannot debug Rust target.+CodeLLDB.+windows-msvc/);

        assert.ok(rustService.buildStub.notCalled);
    });

    test('runs a GNU Rust target without debugging when CodeLLDB is missing', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build', '--target=x86_64-pc-windows-gnu'], '/workspace/api/target/x86_64-pc-windows-gnu/debug/api.exe'),
            [],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'cppvsdbg');
        assert.ok(rustService.buildStub.calledOnce);
    });

    test('detects an ambient Cargo GNU target on a forced Windows host', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const originalTarget = process.env.CARGO_BUILD_TARGET;
        process.env.CARGO_BUILD_TARGET = 'x86_64-pc-windows-gnu';

        try {
            await assert.rejects(
                () => extension.createDebugSessionConfigurationCallback!(
                    createLaunchConfig(['build'], '/workspace/api/target/x86_64-pc-windows-gnu/debug/api.exe'),
                    [],
                    [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                    createDebugConfig()),
                /cannot debug Rust target.+CodeLLDB.+windows-msvc/);
        } finally {
            if (originalTarget === undefined) {
                delete process.env.CARGO_BUILD_TARGET;
            } else {
                process.env.CARGO_BUILD_TARGET = originalTarget;
            }
        }

        assert.ok(rustService.buildStub.notCalled);
    });

    test('detects a GNU target from the resolved executable path on Windows', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], '/workspace/api/target/x86_64-pc-windows-gnullvm/debug/api.exe'),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()),
            /cannot debug Rust target.+CodeLLDB.+windows-msvc/);

        assert.ok(rustService.buildStub.notCalled);
    });

    test('keeps legacy Rust launch metadata on the default Windows adapter', async () => {
        const { rustService, extension } = createExtension(undefined, 'win32');
        const debugConfig = createDebugConfig();
        debugConfig.type = extension.debugAdapter;

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(undefined, '/workspace/api/target/debug/api.exe'),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(rustService.buildStub.calledWith('/workspace/api', ['build'], []));
        assert.strictEqual(debugConfig.type, 'cppvsdbg');
    });

    test('does not persist user-provided Cargo argument values in build diagnostics', async () => {
        const harness = createRustProcessHarness();
        const info = sinon.stub(extensionLogOutputChannel, 'info');

        const build = harness.rustService.build(
            '/workspace/api',
            ['build', '--config', 'registries.private.token=credential-value'],
            [],
            '/workspace/api/target/debug/api');

        harness.childProcess.emit('close', 0, null);
        await build;

        const diagnostics = info.getCalls().map(call => String(call.args[0]));
        assert.ok(diagnostics.some(diagnostic => diagnostic.includes('Building Rust application')));
        assert.ok(diagnostics.some(diagnostic => diagnostic.includes('/workspace/api')));
        assert.ok(diagnostics.every(diagnostic => !diagnostic.includes('credential-value')));
    });

    test('does not persist arbitrary launch configuration environment values', () => {
        const info = sinon.stub(extensionLogOutputChannel, 'info');
        const { extension } = createExtension();
        const credential = 'environment-credential-value';
        const launchConfig = {
            type: 'not-rust',
            environment_variables: [{ name: 'PRIVATE_TOKEN', value: credential }],
        } as ExecutableLaunchConfiguration;

        assert.throws(
            () => extension.getProjectFile(launchConfig),
            error => error instanceof Error && !error.message.includes(credential));
        const diagnostics = info.getCalls().map(call => String(call.args[0]));

        assert.ok(diagnostics.some(diagnostic => diagnostic.includes('launch configuration')));
        assert.ok(diagnostics.every(diagnostic => !diagnostic.includes(credential)));
    });

    test('always redacts resolved Rust environments from persistent configuration logs', async () => {
        const { extension } = createExtension();
        const credential = 'resolved-environment-credential';
        const debugConfig = createDebugConfig();
        debugConfig.env = { PRIVATE_TOKEN: credential };

        await extension.createDebugSessionConfigurationCallback!(
            createLaunchConfig(['build'], '/workspace/api/target/debug/api'),
            [],
            [{ name: 'PRIVATE_TOKEN', value: credential }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);
        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.ok(!JSON.stringify(loggableConfig).includes(credential));
    });
});

function createLaunchConfig(args: string[] | undefined, executablePath: string | undefined): RustLaunchConfiguration {
    return {
        type: 'rust',
        working_directory: '/workspace/api',
        cargo: { args, executable_path: executablePath }
    };
}

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'rust',
        name: 'Rust',
        request: 'launch',
        program: '/workspace/api',
        args: [],
        env: {}
    };
}

function createRustProcessHarness(): {
    childProcess: TestChildProcess;
    disposeSession(): void;
    getDebugOutput(category: 'stdout' | 'stderr'): string;
    rustService: RustService;
} {
    const childProcess = createTestChildProcess();
    sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
    const sendMessage = sinon.stub();
    let sessionDisposable: vscode.Disposable | undefined;
    const debugSession = {
        sendMessage,
        registerPendingStartCancellation: (disposable: vscode.Disposable) => {
            sessionDisposable = disposable;
            return { dispose: sinon.stub() };
        },
    } as unknown as AspireDebugSession;

    return {
        childProcess,
        disposeSession: () => {
            assert.ok(sessionDisposable);
            sessionDisposable.dispose();
        },
        getDebugOutput: category => sendMessage.getCalls()
            .filter(call => call.args[2] === category)
            .map(call => String(call.args[0]))
            .join(''),
        rustService: new RustService(debugSession),
    };
}

function createTestChildProcess(): TestChildProcess {
    return Object.assign(new EventEmitter(), {
        stdin: new PassThrough(),
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        killed: false,
        exitCode: null,
        signalCode: null,
        pid: undefined,
        kill: sinon.stub().returns(true),
    }) as unknown as TestChildProcess;
}

function completeLegacyCargoBuild(
    harness: ReturnType<typeof createRustProcessHarness>,
    executable = '/workspace/api/target/debug/api'
): void {
    harness.childProcess.stdout.write(`${JSON.stringify({
        reason: 'compiler-artifact',
        target: { name: 'api', kind: ['bin'] },
        executable,
    })}\n`);
    harness.childProcess.emit('close', 0, null);
}
