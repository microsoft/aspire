import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { createRustDebuggerExtension, IRustService } from '../debugger/languages/rust';
import { AspireResourceExtendedDebugConfiguration, EnvVar, RustLaunchConfiguration } from '../dcp/types';
import { ResourceDebuggerExtension } from '../debugger/debuggerExtensions';

class TestRustService implements IRustService {
    public buildStub: sinon.SinonStub;

    constructor(error?: Error) {
        this.buildStub = sinon.stub();
        if (error) {
            this.buildStub.rejects(error);
        } else {
            this.buildStub.resolves();
        }
    }

    build(workingDirectory: string, cargoArgs: string[], env: EnvVar[]): Promise<void> {
        return this.buildStub(workingDirectory, cargoArgs, env);
    }
}

suite('Rust Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    const rustExtensionId = process.platform === 'win32' ? 'ms-vscode.cpptools' : 'vadimcn.vscode-lldb';
    const rustDebugAdapter = process.platform === 'win32' ? 'cppvsdbg' : 'lldb';

    teardown(() => sinon.restore());

    function createExtension(error?: Error): { rustService: TestRustService, extension: ResourceDebuggerExtension } {
        const rustService = new TestRustService(error);
        return { rustService, extension: createRustDebuggerExtension(() => rustService) };
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

    test('fails without building when the app host reported no executable', async () => {
        const { rustService, extension } = createExtension();

        await assert.rejects(
            () => extension.createDebugSessionConfigurationCallback!(
                createLaunchConfig(['build'], undefined),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()),
            /did not report which executable/);

        assert.ok(rustService.buildStub.notCalled);
    });
});

function createLaunchConfig(args: string[], executablePath: string | undefined): RustLaunchConfiguration {
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
