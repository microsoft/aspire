import * as assert from 'assert';
import * as sinon from 'sinon';
import { pythonDebuggerExtension } from '../debugger/languages/python';
import { AspireResourceExtendedDebugConfiguration, PythonLaunchConfiguration } from '../dcp/types';
import { AspireDebugSession } from '../debugger/AspireDebugSession';

function createDebugConfig(overrides: Partial<AspireResourceExtendedDebugConfiguration> = {}): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'debugpy',
        name: 'Test Debug Config',
        request: 'launch',
        program: '/apps/myapp/main.py',
        cwd: '/some/default/cwd',
        ...overrides
    };
}

suite('Python Debugger Extension Tests', () => {
    teardown(() => sinon.restore());

    test('working_directory overrides cwd in debug configuration', async () => {
        const launchConfig: PythonLaunchConfiguration = {
            type: 'python',
            program_path: '/apps/myapp/main.py',
            working_directory: '/apps/custom-cwd'
        };

        const debugConfig = createDebugConfig();
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await pythonDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.strictEqual(debugConfig.cwd, '/apps/custom-cwd');
    });

    test('cwd is preserved when working_directory is absent', async () => {
        const launchConfig: PythonLaunchConfiguration = {
            type: 'python',
            program_path: '/apps/myapp/main.py'
        };

        const debugConfig = createDebugConfig({ cwd: '/apps/myapp' });
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await pythonDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.strictEqual(debugConfig.cwd, '/apps/myapp');
    });

    test('module entrypoint sets module and removes program', async () => {
        const launchConfig: PythonLaunchConfiguration = {
            type: 'python',
            module: 'flask',
            working_directory: '/apps/myapp'
        };

        const debugConfig = createDebugConfig();
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await pythonDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.strictEqual(debugConfig.module, 'flask');
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.cwd, '/apps/myapp');
    });

    test('interpreter_path sets python field', async () => {
        const launchConfig: PythonLaunchConfiguration = {
            type: 'python',
            program_path: '/apps/myapp/main.py',
            interpreter_path: '/apps/myapp/.venv/bin/python',
            working_directory: '/apps/myapp'
        };

        const debugConfig = createDebugConfig();
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await pythonDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.strictEqual((debugConfig as any).python, '/apps/myapp/.venv/bin/python');
    });
});
