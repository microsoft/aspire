import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { executeResourceCommand, ResourceCommandRunner } from '../views/resourceCommandExecution';
import { AspireCliFailedError, AspireCliNotInstalledError, ResourceCommandExecutionOutput } from '../data/AppHostDataRepository';
import { resourceCommandLogOpenFailed } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

// Diagnostic log references are only offered as actions when they parse as absolute, and the opened
// Uri round-trips through `fsPath`, so both sides have to use the host separator ('\tmp\cli.log' on
// Windows).
function p(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}

const cliLogPath = p('tmp', 'cli.log');
const appHostLogPath = p('tmp', 'apphost.log');

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

    test('shows a complete CLI failure once and offers its diagnostic logs as actions', async () => {
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Failed to restart resource 'app-id': Failed to stop resource 'app-id'.",
                `📄 See logs at ${cliLogPath}`,
                `🔍 See AppHost logs at ${appHostLogPath}`,
            ].join('\n')));

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        sinon.assert.calledOnceWithExactly(
            errorStub,
            "❌ Failed to restart resource 'app-id': Failed to stop resource 'app-id'.",
            'Open CLI Log',
            'Open AppHost Log');
    });

    test('recognizes localized CLI failures and diagnostic log labels by status icon', async () => {
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Ressource 'app-id' konnte nicht neu gestartet werden.",
                `📄 Diagnoseprotokoll: ${cliLogPath}`,
                `🔍 AppHost-Protokoll: ${appHostLogPath}`,
            ].join('\n')));

        await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        sinon.assert.calledOnceWithExactly(
            errorStub,
            "❌ Ressource 'app-id' konnte nicht neu gestartet werden.",
            'Open CLI Log',
            'Open AppHost Log');
    });

    test('preserves non-absolute log references as diagnostic text', async () => {
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Failed to restart resource 'app-id'.",
                '📄 See logs at relative/cli.log',
            ].join('\n')));

        await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        sinon.assert.calledOnceWithExactly(
            errorStub,
            [
                "❌ Failed to restart resource 'app-id'.",
                '📄 See logs at relative/cli.log',
            ].join('\n'));
    });

    test('completes while the failure notification remains open', async () => {
        errorStub.returns(new Promise<string | undefined>(() => { }));
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            "❌ Failed to restart resource 'app-id'."));

        const execution = executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });
        const result = await Promise.race([
            execution,
            new Promise<'pending'>(resolve => setImmediate(() => resolve('pending'))),
        ]);

        assert.deepStrictEqual(result, { success: false, hadOutput: false });
    });

    test('opens the CLI diagnostic log selected from a failure notification', async () => {
        const document = {} as vscode.TextDocument;
        const openDocumentStub = sandbox.stub(vscode.workspace, 'openTextDocument').resolves(document);
        const showDocumentStub = sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);
        errorStub.resolves('Open CLI Log');
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Failed to restart resource 'app-id': Failed to stop resource 'app-id'.",
                `📄 See logs at ${cliLogPath}`,
            ].join('\n')));

        await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        sinon.assert.calledOnce(openDocumentStub);
        assert.strictEqual((openDocumentStub.firstCall.args[0] as vscode.Uri).fsPath, cliLogPath);
        assert.strictEqual(showDocumentStub.calledOnce, true);
        assert.strictEqual(showDocumentStub.firstCall.args[0], document);
        assert.deepStrictEqual(showDocumentStub.firstCall.args[1], { preview: false });
    });

    test('opens the AppHost diagnostic log selected from a failure notification', async () => {
        const document = {} as vscode.TextDocument;
        const openDocumentStub = sandbox.stub(vscode.workspace, 'openTextDocument').resolves(document);
        const showDocumentStub = sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);
        errorStub.resolves('Open AppHost Log');
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Failed to restart resource 'app-id': Failed to stop resource 'app-id'.",
                `🔍 See AppHost logs at ${appHostLogPath}`,
            ].join('\n')));

        await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        sinon.assert.calledOnce(openDocumentStub);
        assert.strictEqual((openDocumentStub.firstCall.args[0] as vscode.Uri).fsPath, appHostLogPath);
        assert.strictEqual(showDocumentStub.calledOnce, true);
        assert.strictEqual(showDocumentStub.firstCall.args[0], document);
        assert.deepStrictEqual(showDocumentStub.firstCall.args[1], { preview: false });
    });

    test('reports a diagnostic log open failure without rethrowing', async () => {
        sandbox.stub(vscode.workspace, 'openTextDocument').rejects(new Error('access denied'));
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage');
        errorStub.resolves('Open CLI Log');
        const { runner } = makeRunner(new AspireCliFailedError(
            'aspire resource restart',
            16,
            '',
            [
                "❌ Failed to restart resource 'app-id': Failed to stop resource 'app-id'.",
                `📄 See logs at ${cliLogPath}`,
            ].join('\n')));

        const outcome = await executeResourceCommand(
            runner,
            () => { throw new Error('renderer should not be called'); },
            { resourceName: 'app-id', commandName: 'restart', appHostPath: '/repo/apphost.rs' });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(warningStub.calledOnce, true);
        assert.strictEqual(String(warningStub.firstCall.args[0]), resourceCommandLogOpenFailed(cliLogPath, 'access denied'));
    });

    test('reports CLI command output to the user without writing it to extension logs', async () => {
        const logErrorStub = sandbox.stub(extensionLogOutputChannel, 'error');
        const { runner } = makeRunner(new AspireCliFailedError('aspire resource restart', 1, 'stdout secret-token-123', 'stderr visible diagnostic'));

        const outcome = await executeResourceCommand(
            runner,
            () => { },
            { resourceName: 'cache', displayName: 'Cache', commandName: 'restart', appHostPath: '/repo/AppHost.csproj' });

        assert.deepStrictEqual(outcome, { success: false, hadOutput: true });
        assert.strictEqual(errorStub.calledOnce, true);
        assert.match(String(errorStub.firstCall.args[0]), /stderr visible diagnostic/);

        const logMessages = logErrorStub.getCalls().map(call => String(call.args[0])).join('\n');
        assert.match(logMessages, /Command 'restart' on 'cache' failed/);
        assert.doesNotMatch(logMessages, /stdout secret-token-123/);
        assert.doesNotMatch(logMessages, /stderr visible diagnostic/);
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
