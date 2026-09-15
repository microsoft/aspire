import * as assert from 'assert';
import * as vscode from 'vscode';
import * as sinon from 'sinon';
import * as path from 'path';
import net = require('node:net');
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { azureFunctionsNodeDebuggerExtension } from '../debugger/languages/azureFunctions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration, AzureFunctionsNodeLaunchConfiguration } from '../dcp/types';
import { azureFunctionsNodeInspectorPortAllocationFailed } from '../loc/strings';

suite('Azure Functions Node Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => sinon.restore());

    test('advertises Node Functions support when Node debugging is available', () => {
        const capabilities = getSupportedCapabilities();

        assert.ok(capabilities.includes('azure-functions-node'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'azure-functions-node'));
    });

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
        assert.deepStrictEqual(debugConfig.outFiles, [path.join('/workspace/functions', 'dist/**/*.js')]);
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

    test('does not force compiled outFiles for JavaScript functions apps', async () => {
        const taskExecution = { terminate: sinon.spy() } as unknown as vscode.TaskExecution;
        sinon.stub(vscode.tasks, 'executeTask').resolves(taskExecution);
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'func',
            language: 'javascript',
            worker_runtime: 'node'
        };
        const debugConfig = createDebugConfig();

        await azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['host', 'start', '--port', '7071'],
            [{ name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.strictEqual(debugConfig.outFiles, undefined);

        cleanupRun(debugConfig.runId);
    });

    test('preserves configured TypeScript outFiles', async () => {
        const taskExecution = { terminate: sinon.spy() } as unknown as vscode.TaskExecution;
        sinon.stub(vscode.tasks, 'executeTask').resolves(taskExecution);
        const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
            type: 'azure-functions-node',
            app_directory: '/workspace/functions',
            command: 'npm',
            language: 'typescript',
            worker_runtime: 'node'
        };
        const debugConfig = createDebugConfig();
        debugConfig.outFiles = [path.join('/workspace/functions', 'build/**/*.js')];

        await azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', 'start', '--', '--port', '7071'],
            [{ name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig
        );

        assert.deepStrictEqual(debugConfig.outFiles, [path.join('/workspace/functions', 'build/**/*.js')]);

        cleanupRun(debugConfig.runId);
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

    for (const failure of ['null address', 'string address', 'listen error', 'close error'] as const) {
        test(`localizes inspector port allocation failure for ${failure}`, async () => {
            const server = new net.Server();
            const socketError = new Error('Socket failure');
            sinon.stub(net, 'createServer').returns(server);
            sinon.stub(server, 'address').returns(
                failure === 'null address' ? null :
                    failure === 'string address' ? 'pipe' :
                        { address: '127.0.0.1', family: 'IPv4', port: 12345 });
            const close = sinon.stub(server, 'close').callsFake(callback => {
                callback?.(failure === 'close error' ? socketError : undefined);
                return server;
            });
            sinon.stub(server, 'listen').callsFake((...args: unknown[]) => {
                if (failure === 'listen error') {
                    server.emit('error', socketError);
                } else {
                    (args[2] as () => void)();
                }
                return server;
            });
            const executeTask = sinon.stub(vscode.tasks, 'executeTask');
            const launchConfig: AzureFunctionsNodeLaunchConfiguration = {
                type: 'azure-functions-node',
                app_directory: '/workspace/functions',
                command: 'func',
                language: 'javascript',
                worker_runtime: 'node'
            };

            await assert.rejects(
                azureFunctionsNodeDebuggerExtension.createDebugSessionConfigurationCallback!(
                    launchConfig,
                    [], [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                    createDebugConfig()),
                (error: Error) => {
                    assert.strictEqual(error.message, azureFunctionsNodeInspectorPortAllocationFailed);
                    assert.strictEqual(error.cause, failure.endsWith('error') ? socketError : undefined);
                    return true;
                });

            sinon.assert.notCalled(executeTask);
            assert.strictEqual(close.callCount, failure === 'listen error' ? 0 : 1);
        });
    }
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
