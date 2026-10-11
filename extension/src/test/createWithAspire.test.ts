/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWithAspireCommand } from '../commands/createWithAspire';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { errorMessage } from '../loc/strings';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import { AppHostDiscoveryService, type CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { createWorkspaceFolder } from './testHelpers';

interface WorkspaceFolderItem extends vscode.QuickPickItem {
    folder: vscode.WorkspaceFolder;
}

function createAppHostCandidate(folder: vscode.WorkspaceFolder): CandidateAppHostDisplayInfo {
    return {
        path: vscode.Uri.joinPath(folder.uri, 'AppHost.csproj').fsPath,
        language: 'csharp',
        status: 'buildable',
    };
}

suite('createWithAspireCommand', () => {
    let sandbox: sinon.SinonSandbox;
    let showQuickPickStub: sinon.SinonStub;
    let showWorkspaceFolderPickStub: sinon.SinonStub;
    let showErrorMessageStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;
    let workspaceFoldersStub: sinon.SinonStub;
    let activeTextEditorStub: sinon.SinonStub;
    let discoverStub: sinon.SinonStub;
    let discovery: AppHostDiscoveryService;

    setup(() => {
        sandbox = sinon.createSandbox();
        showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(
            async items => (await items).find(item => 'command' in item && item.command === 'aspire-vscode.init'));
        showWorkspaceFolderPickStub = sandbox.stub(vscode.window, 'showWorkspaceFolderPick');
        showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
        workspaceFoldersStub = sandbox.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        activeTextEditorStub = sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake(uri =>
            vscode.workspace.workspaceFolders?.find(folder =>
                uri.toString() === folder.uri.toString() || uri.toString().startsWith(`${folder.uri.toString()}/`)));
        sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
        discovery = new AppHostDiscoveryService(sandbox.createStubInstance(AspireTerminalProvider));
        discoverStub = sandbox.stub(discovery, 'discover').resolves([]);
    });

    teardown(() => {
        discovery.dispose();
        sandbox.restore();
    });

    for (const folders of [undefined, []]) {
        test(`offers both actions when workspace folders are ${folders === undefined ? 'undefined' : 'empty'}`, async () => {
            workspaceFoldersStub.value(folders);

            await createWithAspireCommand(discovery);

            const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
            assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
            assert.strictEqual(discoverStub.called, false);
            assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', windowCliPathTarget, 'tree'));
        });
    }

    test('offers both actions and targets the folder without an AppHost', async () => {
        const folder = createWorkspaceFolder('without-apphost', path.resolve('repo', 'without-apphost'));
        workspaceFoldersStub.value([folder]);

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
        assert.ok(discoverStub.calledOnceWithExactly(folder));
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folder), 'tree'));
    });

    test('hides add-Aspire when the workspace folder has an AppHost', async () => {
        const folder = createWorkspaceFolder('with-apphost', path.resolve('repo', 'with-apphost'));
        workspaceFoldersStub.value([folder]);
        discoverStub.resolves([createAppHostCandidate(folder)]);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new']);
        assert.ok(discoverStub.calledOnceWithExactly(folder));
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    for (const scenario of [
        {
            name: 'multiple AppHosts without a default',
            candidates: [
                { fileName: 'First.AppHost.csproj', language: 'csharp', status: 'buildable' },
                { fileName: 'Second.AppHost.csproj', language: 'csharp', status: 'buildable' },
            ],
        },
        {
            name: 'a possibly-unbuildable AppHost',
            candidates: [
                { fileName: 'apphost.ts', language: 'typescript/nodejs', status: 'possibly-unbuildable' },
            ],
        },
    ]) {
        test(`hides add-Aspire for ${scenario.name}`, async () => {
            const folder = createWorkspaceFolder('with-apphosts', path.resolve('repo', 'with-apphosts'));
            workspaceFoldersStub.value([folder]);
            discoverStub.resolves(scenario.candidates.map(candidate => ({
                path: vscode.Uri.joinPath(folder.uri, candidate.fileName).fsPath,
                language: candidate.language,
                status: candidate.status,
            })));
            const launchService = new AppHostLaunchService({
                getCapabilityStatus: async () => 'supported',
            });
            const provider = new AspireEditorCommandProvider(discovery, launchService);
            showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

            try {
                assert.strictEqual(await provider.getAppHostPath(folder.uri), null);
                await createWithAspireCommand(discovery);

                const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
                assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new']);
                assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
            }
            finally {
                provider.dispose();
                launchService.dispose();
            }
        });
    }

    test('hides add-Aspire when every folder in a multi-root workspace has an AppHost', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        workspaceFoldersStub.value([folderA, folderB]);
        discoverStub.withArgs(folderA).resolves([createAppHostCandidate(folderA)]);
        discoverStub.withArgs(folderB).resolves([createAppHostCandidate(folderB)]);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new']);
        assert.strictEqual(discoverStub.callCount, 2);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    test('targets the only eligible folder in a mixed workspace', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        workspaceFoldersStub.value([folderA, folderB]);
        discoverStub.withArgs(folderA).resolves([createAppHostCandidate(folderA)]);

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
        assert.strictEqual(showQuickPickStub.calledOnce, true);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderB), 'tree'));
    });

    test('targets the eligible folder when the active editor belongs to an initialized folder', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        workspaceFoldersStub.value([folderA, folderB]);
        activeTextEditorStub.value({ document: { uri: vscode.Uri.joinPath(folderA.uri, 'Program.cs') } });
        discoverStub.withArgs(folderA).resolves([createAppHostCandidate(folderA)]);

        await createWithAspireCommand(discovery);

        assert.strictEqual(showQuickPickStub.calledOnce, true);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderB), 'tree'));
    });

    test('preserves the active editor target when its folder is eligible', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        const folderC = createWorkspaceFolder('c', path.resolve('repo', 'c'), 2);
        workspaceFoldersStub.value([folderA, folderB, folderC]);
        activeTextEditorStub.value({ document: { uri: vscode.Uri.joinPath(folderC.uri, 'Program.cs') } });
        discoverStub.withArgs(folderA).resolves([createAppHostCandidate(folderA)]);

        await createWithAspireCommand(discovery);

        assert.strictEqual(showQuickPickStub.calledOnce, true);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderC), 'tree'));
    });

    test('offers only eligible folders in workspace order when the active folder is initialized', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        const folderC = createWorkspaceFolder('c', path.resolve('repo', 'c'), 2);
        workspaceFoldersStub.value([folderA, folderB, folderC]);
        activeTextEditorStub.value({ document: { uri: vscode.Uri.joinPath(folderA.uri, 'Program.cs') } });
        discoverStub.withArgs(folderA).resolves([createAppHostCandidate(folderA)]);
        showQuickPickStub.onSecondCall().callsFake(async (items: WorkspaceFolderItem[]) => items[1]);

        await createWithAspireCommand(discovery);

        assert.deepStrictEqual(showQuickPickStub.secondCall.args[0], [folderB, folderC].map(folder => ({
            label: folder.name,
            description: folder.uri.fsPath,
            folder,
        })));
        assert.strictEqual(showWorkspaceFolderPickStub.called, false);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderC), 'tree'));
    });

    test('distinguishes eligible folders with duplicate names', async () => {
        const folderA = createWorkspaceFolder('app', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('app', path.resolve('repo', 'b'), 1);
        workspaceFoldersStub.value([folderA, folderB]);
        showQuickPickStub.onSecondCall().callsFake(async (items: WorkspaceFolderItem[]) => items[1]);

        await createWithAspireCommand(discovery);

        assert.deepStrictEqual(showQuickPickStub.secondCall.args[0], [folderA, folderB].map(folder => ({
            label: folder.name,
            description: folder.uri.fsPath,
            folder,
        })));
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderB), 'tree'));
    });

    test('throws cancellation before invoking init when the eligible-folder picker is dismissed', async () => {
        workspaceFoldersStub.value([
            createWorkspaceFolder('a', path.resolve('repo', 'a')),
            createWorkspaceFolder('b', path.resolve('repo', 'b'), 1),
        ]);
        showQuickPickStub.onSecondCall().resolves(undefined);

        await assert.rejects(() => createWithAspireCommand(discovery), error => error instanceof vscode.CancellationError);

        assert.strictEqual(showQuickPickStub.callCount, 2);
        assert.strictEqual(executeCommandStub.called, false);
    });

    test('keeps new available but does not initialize a folder whose discovery failed', async () => {
        const folder = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const error = new Error('discovery failed');
        workspaceFoldersStub.value([folder]);
        discoverStub.rejects(error);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new']);
        assert.ok(showErrorMessageStub.calledOnceWithExactly(errorMessage(error)));
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    test('keeps known eligible folders available when discovery fails in another folder', async () => {
        const folderA = createWorkspaceFolder('a', path.resolve('repo', 'a'));
        const folderB = createWorkspaceFolder('b', path.resolve('repo', 'b'), 1);
        const error = new Error('discovery failed');
        workspaceFoldersStub.value([folderA, folderB]);
        discoverStub.withArgs(folderA).rejects(error);

        await createWithAspireCommand(discovery);

        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
        assert.ok(showErrorMessageStub.calledOnceWithExactly(errorMessage(error)));
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', workspaceFolderCliPathTarget(folderB), 'tree'));
    });

    test('propagates discovery cancellation without offering setup actions', async () => {
        workspaceFoldersStub.value([createWorkspaceFolder('a', path.resolve('repo', 'a'))]);
        discoverStub.rejects(new vscode.CancellationError());

        await assert.rejects(() => createWithAspireCommand(discovery), error => error instanceof vscode.CancellationError);

        assert.strictEqual(showQuickPickStub.called, false);
        assert.strictEqual(showErrorMessageStub.called, false);
        assert.strictEqual(executeCommandStub.called, false);
    });

    test('returns the handled cancellation from delegated new', async () => {
        const handledCancellation = { success: false as const, canceled: true };
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));
        executeCommandStub.resolves(handledCancellation);

        const result = await createWithAspireCommand(discovery);

        assert.strictEqual(result, handledCancellation);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    test('returns the handled error from delegated init', async () => {
        const handledError = { success: false as const, errorKind: 'Error' };
        executeCommandStub.resolves(handledError);

        const result = await createWithAspireCommand(discovery);

        assert.strictEqual(result, handledError);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', windowCliPathTarget, 'tree'));
    });

    test('throws cancellation when the action picker is dismissed', async () => {
        showQuickPickStub.resolves(undefined);

        await assert.rejects(() => createWithAspireCommand(discovery), error => error instanceof vscode.CancellationError);

        assert.strictEqual(executeCommandStub.called, false);
    });
});
