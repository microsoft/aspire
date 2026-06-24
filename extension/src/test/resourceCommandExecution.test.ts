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
        const rendered: Array<[string, string, string, string | undefined]> = [];

        const outcome = await executeResourceCommand(
            runner,
            (resource, command, content, appHostPath) => { rendered.push([resource, command, content, appHostPath]); },
            { resourceName: 'cache', commandName: 'describe', appHostPath: '/repo/AppHost.csproj' });

        assert.deepStrictEqual(outcome, { success: true, hadOutput: true });
        assert.deepStrictEqual(rendered, [['cache', 'describe', 'line one\nline two', '/repo/AppHost.csproj']]);
        assert.strictEqual(infoStub.calledOnce, true);
    });

    test('reports CLI command failure using stderr details without rethrowing', async () => {
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
        assert.match(message, /more detail/);
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

    test('reports a CLI-not-installed failure distinctly without rethrowing', async () => {
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

    test('does not report a command failure when output rendering fails after command success', async () => {
        const { runner } = makeRunner({ stdout: 'command output', stderr: '' });

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('editor failed'); },
            { resourceName: 'cache', commandName: 'describe', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: true, hadOutput: false });
        assert.strictEqual(infoStub.calledOnce, true);
        assert.strictEqual(errorStub.calledOnce, true);
        assert.match(String(errorStub.firstCall.args[0]), /editor failed/);
        assert.ok(!String(errorStub.firstCall.args[0]).includes("Command 'describe' on 'cache' failed"));
    });

    test('passes the progress cancellation token to the resource command runner', async () => {
        const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => { } }) } as vscode.CancellationToken;
        (vscode.window.withProgress as sinon.SinonStub).restore();
        const withProgressStub = sandbox.stub(vscode.window, 'withProgress').callsFake((options: any, task: any) => task({ report: () => { } }, token));
        let observedToken: vscode.CancellationToken | undefined;
        const runner: ResourceCommandRunner = {
            runResourceCommand: async (_resourceName, _appHostPath, _commandName, _additionalArgs, cancellationToken?: vscode.CancellationToken) => {
                observedToken = cancellationToken;
                return { stdout: '', stderr: '' };
            },
        };

        await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'cache', commandName: 'start', appHostPath: undefined });

        assert.strictEqual(withProgressStub.firstCall.args[0].cancellable, true);
        assert.strictEqual(observedToken, token);
    });

    test('failure detail skips progress noise and preserves validation lines', async () => {
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource reset',
            1,
            '',
            [
                "Validating and executing command 'reset' on resource 'cache'...",
                "Failed to validate command arguments for command 'reset' on resource 'cache':",
                "--message: Value is required.",
                "--count: Enter a number."
            ].join('\n')));

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'cache', commandName: 'reset', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(errorStub.calledOnce, true);
        const message = String(errorStub.firstCall.args[0]);
        assert.ok(!message.includes('Validating and executing'), message);
        assert.match(message, /Failed to validate command arguments/);
        assert.match(message, /--message: Value is required/);
        assert.match(message, /--count: Enter a number/);
    });

    test('generic failure notification detail is bounded', async () => {
        const { runner } = makeRunner(new Error('x'.repeat(3000)));

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'cache', commandName: 'reset', appHostPath: undefined });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(errorStub.calledOnce, true);
        const message = String(errorStub.firstCall.args[0]);
        assert.ok(message.length < 2500, `Expected bounded detail, got ${message.length} characters.`);
        assert.ok(message.endsWith('...'), message);
    });

    test('preserves cancellation by rethrowing without showing an error', async () => {
        const { runner } = makeRunner(new vscode.CancellationError());

        await assert.rejects(
            () => executeResourceCommand(
                runner,
                () => { throw new Error('renderer should not be called'); },
                { resourceName: 'cache', commandName: 'reset', appHostPath: undefined }),
            vscode.CancellationError);

        assert.strictEqual(errorStub.called, false);
        assert.strictEqual(infoStub.called, false);
    });
});
