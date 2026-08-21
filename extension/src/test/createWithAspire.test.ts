/// <reference types="mocha" />

import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWithAspireCommand } from '../commands/createWithAspire';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { selectWorkspaceFolderForAspireCommand } from '../loc/strings';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

function createWorkspaceFolder(name: string, index: number): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(`/repo/${name}`),
        name,
        index,
    };
}

function createEditorCommandProvider(eligibleFolders: readonly vscode.WorkspaceFolder[]): AspireEditorCommandProvider {
    return {
        getWorkspaceFoldersWithoutAppHosts: async () => eligibleFolders,
    } as unknown as AspireEditorCommandProvider;
}

suite('createWithAspireCommand', () => {
    let sandbox: sinon.SinonSandbox;
    let showQuickPickStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;

    setup(() => {
        sandbox = sinon.createSandbox();
        showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick');
        executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('offers only the new-app option when every workspace folder already has an AppHost', async () => {
        await createWithAspireCommand(createEditorCommandProvider([]));

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.strictEqual(items.length, 1);
        assert.strictEqual(items[0].command, 'aspire-vscode.new');
    });

    test('also offers the add-to-workspace option when an applicable folder lacks an AppHost', async () => {
        await createWithAspireCommand(createEditorCommandProvider([createWorkspaceFolder('eligible', 0)]));

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.strictEqual(items.length, 2);
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
    });

    test('discovers eligible workspace folders exactly once per command execution', async () => {
        const folder = createWorkspaceFolder('eligible', 0);
        const getEligibleFoldersStub = sinon.stub().resolves([folder]);
        const editorCommandProvider = {
            getWorkspaceFoldersWithoutAppHosts: getEligibleFoldersStub,
        } as unknown as AspireEditorCommandProvider;
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand(editorCommandProvider);

        assert.strictEqual(getEligibleFoldersStub.calledOnce, true);
    });

    test('invokes aspire-vscode.new when the new-app option is selected', async () => {
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand(createEditorCommandProvider([createWorkspaceFolder('eligible', 0)]));

        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new'));
    });

    test('passes the sole eligible workspace folder target to aspire-vscode.init', async () => {
        const folder = createWorkspaceFolder('eligible', 0);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand(createEditorCommandProvider([folder]));

        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folder)));
    });

    test('uses the active eligible workspace folder without showing another picker', async () => {
        const firstFolder = createWorkspaceFolder('first', 0);
        const eligibleSecondFolder = createWorkspaceFolder('second', 1);
        const activeSecondFolder = createWorkspaceFolder('second', 1);
        const activeEditor = {
            document: {
                uri: vscode.Uri.file('/repo/second/file.ts'),
            },
        } as vscode.TextEditor;
        sandbox.stub(vscode.window, 'activeTextEditor').value(activeEditor);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(activeEditor.document.uri).returns(activeSecondFolder);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand(createEditorCommandProvider([firstFolder, eligibleSecondFolder]));

        assert.strictEqual(showQuickPickStub.callCount, 1);
        assert.ok(executeCommandStub.calledOnce);
        assert.strictEqual(executeCommandStub.firstCall.args[0], 'aspire-vscode.init');
        assert.strictEqual(executeCommandStub.firstCall.args[1].workspaceFolder, eligibleSecondFolder);
    });

    test('prompts with only eligible folders and forwards the selected target when multiple are eligible', async () => {
        const firstFolder = createWorkspaceFolder('first', 0);
        const secondFolder = createWorkspaceFolder('second', 1);
        const ineligibleFolder = createWorkspaceFolder('ineligible', 2);
        const activeEditor = {
            document: {
                uri: vscode.Uri.file('/repo/ineligible/file.ts'),
            },
        } as vscode.TextEditor;
        sandbox.stub(vscode.window, 'activeTextEditor').value(activeEditor);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(activeEditor.document.uri).returns(ineligibleFolder);
        showQuickPickStub.onFirstCall().callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));
        showQuickPickStub.onSecondCall().callsFake(async (items: { workspaceFolder: vscode.WorkspaceFolder }[]) => items.find(item => item.workspaceFolder === secondFolder));

        await createWithAspireCommand(createEditorCommandProvider([firstFolder, secondFolder]));

        assert.strictEqual(showQuickPickStub.callCount, 2);
        const folderItems = showQuickPickStub.secondCall.args[0] as { workspaceFolder: vscode.WorkspaceFolder }[];
        assert.deepStrictEqual(folderItems.map(item => item.workspaceFolder), [firstFolder, secondFolder]);
        assert.strictEqual(showQuickPickStub.secondCall.args[1].placeHolder, selectWorkspaceFolderForAspireCommand);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(secondFolder)));
    });

    test('does nothing when the outcome picker is dismissed', async () => {
        showQuickPickStub.resolves(undefined);

        await createWithAspireCommand(createEditorCommandProvider([createWorkspaceFolder('eligible', 0)]));

        assert.strictEqual(executeCommandStub.called, false);
    });

    test('does nothing when the eligible workspace folder picker is dismissed', async () => {
        const firstFolder = createWorkspaceFolder('first', 0);
        const secondFolder = createWorkspaceFolder('second', 1);
        showQuickPickStub.onFirstCall().callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));
        showQuickPickStub.onSecondCall().resolves(undefined);

        await createWithAspireCommand(createEditorCommandProvider([firstFolder, secondFolder]));

        assert.strictEqual(showQuickPickStub.callCount, 2);
        assert.strictEqual(executeCommandStub.called, false);
    });
});
