import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { executeResourceCommand, ResourceCommandRunner } from '../views/resourceCommandExecution';
import { AspireCliFailedError, AspireCliNotInstalledError, ResourceCommandExecutionOutput } from '../views/AppHostDataRepository';

suite('executeResourceCommand', () => {
    let sandbox: sinon.SinonSandbox;
    let infoStub: sinon.SinonStub;
    let errorStub: sinon.SinonStub;

    setup(() => {
        sandbox = sinon.createSandbox();
        // Run the progress task synchronously so the test does not depend on the notification UI.
        sandbox.stub(vscode.window, 'withProgress').callsFake((_options: any, task: any) => task({ report: () => { } }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => { } }) }));
        infoStub = sandbox.stub(vscode.window, 'showInformationMessage');
        errorStub = sandbox.stub(vscode.window, 'showErrorMessage');
    });

    teardown(() => {
        sandbox.restore();
    });

    function makeRunner(result: ResourceCommandExecutionOutput | Error): { runner: ResourceCommandRunner; calls: Array<[string, string | undefined, string, readonly string[]]> } {
        const calls: Array<[string, string | undefined, string, readonly string[]]> = [];
        const runner: ResourceCommandRunner = {
            runResourceCommand: async (resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = []) => {
                calls.push([resourceName, appHostPath, commandName, additionalArgs]);
                if (result instanceof Error) {
                    throw result;
                }

                return result;
            },
        };
        return { runner, calls };
    }

    test('forwards the request to the runner and reports success without output', async () => {
        const { runner, calls } = makeRunner({ stdout: '', stderr: '' });
        const rendered: Array<[string, string, string]> = [];

        const outcome = await executeResourceCommand(
            runner,
            (resource, command, content) => { rendered.push([resource, command, content]); },
            { resourceName: 'cache', commandName: 'restart', appHostPath: '/repo/AppHost.csproj', additionalArgs: [] });

        assert.deepStrictEqual(calls, [['cache', '/repo/AppHost.csproj', 'restart', []]]);
        assert.deepStrictEqual(outcome, { success: true, hadOutput: false });
        assert.strictEqual(infoStub.calledOnce, true);
        assert.strictEqual(errorStub.called, false);
        assert.deepStrictEqual(rendered, []);
    });

    test('renders returned command output when stdout is non-empty', async () => {
        const { runner } = makeRunner({ stdout: 'line one\nline two', stderr: '' });
        const rendered: Array<[string, string, string]> = [];

        const outcome = await executeResourceCommand(
            runner,
            (resource, command, content) => { rendered.push([resource, command, content]); },
            { resourceName: 'cache', commandName: 'describe', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: true, hadOutput: true });
        assert.deepStrictEqual(rendered, [['cache', 'describe', 'line one\nline two']]);
        assert.strictEqual(infoStub.calledOnce, true);
    });

    test('reports CLI command failure using the first line of stderr', async () => {
        const { runner } = makeRunner(new AspireCliFailedError('aspire resource restart', 1, '', 'resource is disabled\nmore detail'));
        const rendered: Array<[string, string, string]> = [];

        const outcome = await executeResourceCommand(
            runner,
            (resource, command, content) => { rendered.push([resource, command, content]); },
            { resourceName: 'cache', displayName: 'Cache', commandName: 'restart', appHostPath: '/repo/AppHost.csproj' });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(errorStub.calledOnce, true);
        const message = String(errorStub.firstCall.args[0]);
        assert.match(message, /resource is disabled/);
        assert.ok(!message.includes('more detail'), 'Only the first line of stderr should be surfaced');
        assert.strictEqual(infoStub.called, false);
        assert.deepStrictEqual(rendered, []);
    });

    test('renders captured stdout even when the CLI command fails', async () => {
        const { runner } = makeRunner(new AspireCliFailedError('aspire resource echo', 2, 'partial output', 'boom'));
        const rendered: Array<[string, string, string]> = [];

        const outcome = await executeResourceCommand(
            runner,
            (resource, command, content) => { rendered.push([resource, command, content]); },
            { resourceName: 'cache', commandName: 'echo', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: true });
        assert.deepStrictEqual(rendered, [['cache', 'echo', 'partial output']]);
        assert.strictEqual(errorStub.calledOnce, true);
    });

    test('reports a CLI-not-installed failure distinctly', async () => {
        const { runner } = makeRunner(new AspireCliNotInstalledError('aspire not found on PATH'));

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'cache', commandName: 'start', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(errorStub.calledOnce, true);
        assert.match(String(errorStub.firstCall.args[0]), /aspire not found on PATH/);
        assert.strictEqual(infoStub.called, false);
    });
});
