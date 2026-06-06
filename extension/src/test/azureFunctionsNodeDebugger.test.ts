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

    test('starts functions host task and attaches to node inspector port', async () => {
        const taskExecution = { terminate: sinon.spy() } as unknown as vscode.TaskExecution;
        const executeTaskStub = sinon.stub(vscode.tasks, 'executeTask').resolves(taskExecution);
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'npm',
            debug_port: '9230',
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
        assert.strictEqual(execution.options?.env?.languageWorkers__node__arguments, '--inspect=9230');

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.request, 'attach');
        assert.strictEqual(debugConfig.address, 'localhost');
        assert.strictEqual(debugConfig.port, 9230);
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

    test('rejects invalid node functions launch configuration', async () => {
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'npm',
            debug_port: 'not-a-port',
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
