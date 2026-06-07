import * as assert from 'assert';
import * as vscode from 'vscode';
import * as sinon from 'sinon';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { azureFunctionsNodeDebuggerExtension } from '../debugger/languages/azureFunctions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration, AzureFunctionsNodeLaunchConfiguration } from '../dcp/types';

suite('Azure Functions Node Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => sinon.restore());

    test('starts functions host task and attaches to debug-only node inspector port', async () => {
        const taskExecution = { terminate: sinon.spy() } as unknown as vscode.TaskExecution;
        const executeTaskStub = sinon.stub(vscode.tasks, 'executeTask').resolves(taskExecution);
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'npm',
            language: 'typescript',
            worker_runtime: 'node'
        };
        const args = ['run', 'start', '--', '--port', '7071'];
        const debugConfig = createDebugConfig();

        await azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            args,
            [{ name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        sinon.assert.calledOnce(executeTaskStub);
        const task = executeTaskStub.firstCall.args[0];
        const execution = task.execution as vscode.ShellExecution;

        assert.strictEqual(task.name, 'func: functions');
        assert.strictEqual(execution.command, 'npm');
        assert.deepStrictEqual(execution.args, args);
        assert.strictEqual(execution.options?.cwd, '/workspace/functions');
        assert.strictEqual(execution.options?.env?.FUNCTIONS_WORKER_RUNTIME, 'node');
        const workerArguments = execution.options?.env?.languageWorkers__node__arguments;
        assert.match(workerArguments ?? '', /^--inspect=127\.0\.0\.1:\d+$/);
        const debugPort = Number.parseInt(workerArguments!.substring(workerArguments!.lastIndexOf(':') + 1), 10);
        assert.ok(Number.isInteger(debugPort));
        assert.ok(debugPort > 0);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.request, 'attach');
        assert.strictEqual(debugConfig.address, '127.0.0.1');
        assert.strictEqual(debugConfig.port, debugPort);
        assert.strictEqual(debugConfig.restart, true);
        assert.strictEqual(debugConfig.sourceMaps, true);
        assert.strictEqual(debugConfig.cwd, '/workspace/functions');
        assert.deepStrictEqual(debugConfig.outFiles, ['/workspace/functions/dist/**/*.js']);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
        assert.strictEqual(debugConfig.env, undefined);

        cleanupRun(debugConfig.runId);
        sinon.assert.calledOnce(taskExecution.terminate as sinon.SinonSpy);
    });

    test('configures no-debug launch without starting inspector task', async () => {
        const executeTaskStub = sinon.stub(vscode.tasks, 'executeTask');
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'npm',
            language: 'typescript',
            worker_runtime: 'node'
        };
        const args = ['run', 'start', '--', '--port', '7071'];
        const debugConfig = createDebugConfig();

        await azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            args,
            [{ name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        sinon.assert.notCalled(executeTaskStub);
        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.runtimeExecutable, 'npm');
        assert.deepStrictEqual(debugConfig.runtimeArgs, args);
        assert.strictEqual(debugConfig.cwd, '/workspace/functions');
        assert.strictEqual(debugConfig.noDebug, true);
        assert.strictEqual(debugConfig.port, undefined);
        assert.strictEqual(debugConfig.env?.languageWorkers__node__arguments, undefined);
    });

    test('rejects invalid node functions launch configuration', async () => {
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: '',
            language: 'typescript',
            worker_runtime: 'node'
        };

        await assert.rejects(
            azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()
            )
        );
    });
});

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'pwa-node',
        name: 'Azure Functions',
        request: 'launch',
        program: '/workspace/functions',
        args: [],
        env: {}
    };
}
