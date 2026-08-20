import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { doCommand } from '../commands/do';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { pipelineStepRequired } from '../loc/strings';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { resolvePipelineStep } from '../utils/pipelineStep';

suite('pipeline step resolution', () => {
    let sandbox: sinon.SinonSandbox;
    let configInfoProvider: ConfigInfoProvider;
    let hasCapabilityStub: sinon.SinonStub;
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
        hasCapabilityStub = sandbox.stub().resolves(false);
        configInfoProvider = {
            hasCapability: hasCapabilityStub,
        } as unknown as ConfigInfoProvider;
    });

    teardown(() => {
        sandbox.restore();
    });

    test('capable CLI uses its interaction service with the exact target and CLI path', async () => {
        hasCapabilityStub.resolves(true);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, null);
        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', {
            target,
            cliPath,
            suppressErrors: true,
            forceRefresh: true,
        }));
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('known interaction support does not re-probe the CLI', async () => {
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath, true);

        assert.strictEqual(step, null);
        assert.strictEqual(hasCapabilityStub.called, false);
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('known legacy support uses local input without re-probing the CLI', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  deploy  ');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath, false);

        assert.strictEqual(step, 'deploy');
        assert.strictEqual(hasCapabilityStub.called, false);
    });

    test('legacy CLI trims locally entered pipeline steps', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  deploy  ');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, 'deploy');
    });

    test('legacy CLI rejects whitespace-only pipeline steps with the localized validation message', async () => {
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').callsFake(async options => {
            assert.strictEqual(await options?.validateInput?.('   '), pipelineStepRequired);
            return undefined;
        });

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, undefined);
        assert.strictEqual(showInputBoxStub.calledOnce, true);
    });

    test('input cancellation returns undefined', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, undefined);
    });

    test('non-cancellation errors propagate', async () => {
        const error = new Error('capability probe failed');
        hasCapabilityStub.rejects(error);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        await assert.rejects(resolvePipelineStep(configInfoProvider, target, cliPath), error);

        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('doCommand preserves its five arguments through resolution and launch', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  release  ');
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await doCommand(configInfoProvider, editorCommandProvider, appHostPath, target, cliPath);

        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', {
            target,
            cliPath,
            suppressErrors: true,
            forceRefresh: true,
        }));
        assert.ok(tryExecuteDoAppHostStub.calledOnceWithExactly(false, 'release', appHostPath, target, cliPath));
    });

    test('doCommand treats pipeline-step cancellation as cancellation without launching', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await assert.rejects(
            doCommand(configInfoProvider, editorCommandProvider, appHostPath, target, cliPath),
            error => error instanceof vscode.CancellationError);

        assert.strictEqual(tryExecuteDoAppHostStub.called, false);
    });
});
