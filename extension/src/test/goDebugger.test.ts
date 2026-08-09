import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { goDebuggerExtension } from '../debugger/languages/go';
import { AspireResourceExtendedDebugConfiguration, GoLaunchConfiguration } from '../dcp/types';

suite('Go Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => sinon.restore());

    test('advertises Go support when the Go extension is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            return extensionId === 'golang.go' ? { id: extensionId } as vscode.Extension<unknown> : undefined;
        });

        const capabilities = getSupportedCapabilities();
        assert.ok(capabilities.includes('go'));
        assert.ok(capabilities.includes('golang.go'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'go'));
    });

    test('configures VS Code Go debugger with dlv-dap', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            program: '/workspace/api/cmd/server',
            working_directory: '/workspace/api',
            build_flags: "-tags='integration' -gcflags='all=-N -l'"
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['--listen', ':8080'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'go');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.mode, 'debug');
        assert.strictEqual(debugConfig.debugAdapter, 'dlv-dap');
        assert.strictEqual(debugConfig.program, '/workspace/api/cmd/server');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
        assert.strictEqual(debugConfig.buildFlags, "-tags='integration' -gcflags='all=-N -l'");
        assert.deepStrictEqual(debugConfig.args, ['--listen', ':8080']);
        assert.strictEqual(debugConfig.noDebug, false);
    });

    test('sets noDebug when launch option disables debugging', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            program: '/workspace/api',
            working_directory: '/workspace/api'
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('uses working directory as program when program is absent', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            working_directory: '/workspace/api'
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.program, '/workspace/api');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
    });
});

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'go',
        name: 'Go',
        request: 'launch',
        program: '/workspace/api',
        args: []
    };
}
