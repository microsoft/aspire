import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { doCommand } from '../commands/do';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { pipelineStepRequired } from '../loc/strings';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { resolvePipelineStep } from '../utils/pipelineStep';

suite('pipeline step resolution', () => {
    let sandbox: sinon.SinonSandbox;
    let terminalProvider: AspireTerminalProvider;
    const cliPath = '/repo/b/tools/aspire';
    const appHostPath = '/repo/b/AppHost/AppHost.csproj';
    const workspaceFolder: vscode.WorkspaceFolder = {
        uri: vscode.Uri.file('/repo/b'),
        name: 'b',
        index: 1,
    };
    const target = workspaceFolderCliPathTarget(workspaceFolder);

    setup(() => {
        sandbox = sinon.createSandbox();
        terminalProvider = {} as AspireTerminalProvider;
    });

    teardown(() => {
        sandbox.restore();
    });

    test('capable CLI uses its interaction service with the exact target and CLI path', async () => {
        const hasCapabilityStub = sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(true);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await resolvePipelineStep(terminalProvider, target, cliPath);

        assert.strictEqual(step, null);
        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', { target, cliPath, suppressErrors: true }));
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('legacy CLI trims locally entered pipeline steps', async () => {
        sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(false);
        sandbox.stub(vscode.window, 'showInputBox').resolves('  deploy  ');

        const step = await resolvePipelineStep(terminalProvider, target, cliPath);

        assert.strictEqual(step, 'deploy');
    });

    test('legacy CLI rejects whitespace-only pipeline steps with the localized validation message', async () => {
        sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(false);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').callsFake(async options => {
            assert.strictEqual(await options?.validateInput?.('   '), pipelineStepRequired);
            return undefined;
        });

        const step = await resolvePipelineStep(terminalProvider, target, cliPath);

        assert.strictEqual(step, undefined);
        assert.strictEqual(showInputBoxStub.calledOnce, true);
    });

    test('input cancellation returns undefined', async () => {
        sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(false);
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);

        const step = await resolvePipelineStep(terminalProvider, target, cliPath);

        assert.strictEqual(step, undefined);
    });

    test('non-cancellation errors propagate', async () => {
        const error = new Error('capability probe failed');
        sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').rejects(error);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        await assert.rejects(resolvePipelineStep(terminalProvider, target, cliPath), error);

        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('doCommand preserves its five arguments through resolution and launch', async () => {
        const hasCapabilityStub = sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(false);
        sandbox.stub(vscode.window, 'showInputBox').resolves('  release  ');
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await doCommand(terminalProvider, editorCommandProvider, appHostPath, target, cliPath);

        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', { target, cliPath, suppressErrors: true }));
        assert.ok(tryExecuteDoAppHostStub.calledOnceWithExactly(false, 'release', appHostPath, target, cliPath));
    });

    test('doCommand treats pipeline-step cancellation as cancellation without launching', async () => {
        sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(false);
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await assert.rejects(
            doCommand(terminalProvider, editorCommandProvider, appHostPath, target, cliPath),
            error => error instanceof vscode.CancellationError);

        assert.strictEqual(tryExecuteDoAppHostStub.called, false);
    });
});
