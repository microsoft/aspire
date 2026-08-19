/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { registerCliCommands } from '../activation/registerCliCommands';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliPathModule from '../utils/cliPath';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { createWorkspaceFolder, removeDirectorySafely } from './testHelpers';
suite('registerCliCommands', () => {
    let sandbox: sinon.SinonSandbox;
    let callbacks: Map<string, (...args: unknown[]) => Promise<unknown>>;
    let terminalProvider: AspireTerminalProvider;
    let sendCommandStub: sinon.SinonStub;
    let getTerminalStub: sinon.SinonStub;
    let resolveCliPathStub: sinon.SinonStub;
    let showWorkspaceFolderPickStub: sinon.SinonStub;
    let workspaceFoldersStub: sinon.SinonStub;
    let activeTextEditorStub: sinon.SinonStub;
    let getWorkspaceFolderStub: sinon.SinonStub;
    let getAppHostPathStub: sinon.SinonStub;
    let tryExecuteDoAppHostStub: sinon.SinonStub;
    let editorCommandProvider: AspireEditorCommandProvider;
    let tempDir: string;

    setup(() => {
        sandbox = sinon.createSandbox();
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-commands-'));
        callbacks = new Map();
        sandbox.stub(vscode.commands, 'registerCommand').callsFake((command, callback) => {
            callbacks.set(command, callback as (...args: unknown[]) => Promise<unknown>);
            return { dispose: () => { } };
        });
        sendCommandStub = sinon.stub().resolves();
        getTerminalStub = sinon.stub().returns({
            terminal: { show: sinon.stub() },
            dispose: () => { },
        });
        terminalProvider = {
            sendAspireCommandToAspireTerminal: sendCommandStub,
            getAspireTerminal: getTerminalStub,
        } as unknown as AspireTerminalProvider;
        resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: '/resolved/aspire',
            available: true,
            source: 'configured',
        });
        showWorkspaceFolderPickStub = sandbox.stub(vscode.window, 'showWorkspaceFolderPick');
        workspaceFoldersStub = sandbox.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        activeTextEditorStub = sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);
        getWorkspaceFolderStub = sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        getAppHostPathStub = sinon.stub().resolves(null);
        tryExecuteDoAppHostStub = sinon.stub().resolves();
        editorCommandProvider = {
            getAppHostPath: getAppHostPathStub,
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        registerCliCommands(terminalProvider, editorCommandProvider);
    });

    teardown(() => {
        sandbox.restore();
        removeDirectorySafely(tempDir);
    });

    test('init uses the active editor workspace folder without prompting', async () => {
        const folderA = createWorkspaceFolder('a', '/repo/a');
        const folderB = createWorkspaceFolder('b', '/repo/b');
        const target = workspaceFolderCliPathTarget(folderB);
        workspaceFoldersStub.value([folderA, folderB]);
        activeTextEditorStub.value({ document: { uri: vscode.Uri.file('/repo/b/Program.cs') } });
        getWorkspaceFolderStub.returns(folderB);

        await callbacks.get('aspire-vscode.init')!();

        assert.strictEqual(showWorkspaceFolderPickStub.called, false);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(sendCommandStub.calledOnceWith('init', true, undefined, { target, cliPath: '/resolved/aspire' }));
    });

    test('new prompts once in a multi-root window and reuses the selected target', async () => {
        const folderA = createWorkspaceFolder('a', '/repo/a');
        const folderB = createWorkspaceFolder('b', '/repo/b');
        const target = workspaceFolderCliPathTarget(folderB);
        workspaceFoldersStub.value([folderA, folderB]);
        showWorkspaceFolderPickStub.resolves(folderB);

        await callbacks.get('aspire-vscode.new')!();

        assert.strictEqual(showWorkspaceFolderPickStub.calledOnce, true);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(sendCommandStub.calledOnceWith('new', true, undefined, { target, cliPath: '/resolved/aspire' }));
    });

    test('workspace folder selection cancellation prevents the gate and command body', async () => {
        workspaceFoldersStub.value([
            createWorkspaceFolder('a', '/repo/a'),
            createWorkspaceFolder('b', '/repo/b'),
        ]);
        showWorkspaceFolderPickStub.resolves(undefined);

        await callbacks.get('aspire-vscode.init')!();

        assert.strictEqual(showWorkspaceFolderPickStub.calledOnce, true);
        assert.strictEqual(resolveCliPathStub.called, false);
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('open terminal uses the only workspace folder without prompting', async () => {
        const folder = createWorkspaceFolder('a', '/repo/a');
        const target = workspaceFolderCliPathTarget(folder);
        workspaceFoldersStub.value([folder]);

        await callbacks.get('aspire-vscode.openTerminal')!();

        assert.strictEqual(showWorkspaceFolderPickStub.called, false);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(getTerminalStub.calledOnceWith(false, target, '/resolved/aspire'));
    });

    test('init uses the window target when no workspace folders are open', async () => {
        await callbacks.get('aspire-vscode.init')!();

        assert.strictEqual(showWorkspaceFolderPickStub.called, false);
        assert.ok(resolveCliPathStub.calledOnceWith(windowCliPathTarget));
        assert.ok(sendCommandStub.calledOnceWith('init', true, undefined, { target: windowCliPathTarget, cliPath: '/resolved/aspire' }));
    });

    test('add resolves the AppHost once and uses its workspace target for gate and terminal', async () => {
        const folder = createWorkspaceFolder('a', '/repo/a');
        const appHostPath = '/repo/a/AppHost/AppHost.csproj';
        const target = workspaceFolderCliPathTarget(folder);
        getAppHostPathStub.resolves(appHostPath);
        getWorkspaceFolderStub.returns(folder);

        await callbacks.get('aspire-vscode.add')!();

        assert.strictEqual(getAppHostPathStub.calledOnce, true);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(sendCommandStub.calledOnceWith('add', true, ['--apphost', appHostPath], { target, cliPath: '/resolved/aspire' }));
    });

    test('do uses the resolved AppHost target for capability probing and execution', async () => {
        const folder = createWorkspaceFolder('a', '/repo/a');
        const appHostPath = '/repo/a/AppHost/AppHost.csproj';
        const target = workspaceFolderCliPathTarget(folder);
        getAppHostPathStub.resolves(appHostPath);
        getWorkspaceFolderStub.returns(folder);
        const hasCapabilityStub = sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(true);

        await callbacks.get('aspire-vscode.do')!();

        assert.strictEqual(getAppHostPathStub.calledOnce, true);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(hasCapabilityStub.calledOnceWith('pipelines', { target, cliPath: '/resolved/aspire' }));
        assert.ok(tryExecuteDoAppHostStub.calledOnceWith(false, undefined, appHostPath, target, '/resolved/aspire'));
    });

    test('do rejects a missing AppHost before probing the CLI', async () => {
        const hasCapabilityStub = sandbox.stub(ConfigInfoProvider.prototype, 'hasCapability').resolves(true);
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        await callbacks.get('aspire-vscode.do')!();

        assert.strictEqual(getAppHostPathStub.calledOnce, true);
        assert.strictEqual(resolveCliPathStub.called, false);
        assert.strictEqual(hasCapabilityStub.called, false);
        assert.strictEqual(tryExecuteDoAppHostStub.called, false);
        assert.strictEqual(showErrorMessageStub.calledOnce, true);
    });

    test('add without an AppHost selects one workspace folder for the gate and terminal', async () => {
        const folderA = createWorkspaceFolder('a', '/repo/a');
        const folderB = createWorkspaceFolder('b', '/repo/b');
        const target = workspaceFolderCliPathTarget(folderB);
        workspaceFoldersStub.value([folderA, folderB]);
        showWorkspaceFolderPickStub.resolves(folderB);

        await callbacks.get('aspire-vscode.add')!();

        assert.strictEqual(getAppHostPathStub.calledOnce, true);
        assert.strictEqual(showWorkspaceFolderPickStub.calledOnce, true);
        assert.ok(resolveCliPathStub.calledOnceWith(target));
        assert.ok(sendCommandStub.calledOnceWith('add', true, undefined, { target, cliPath: '/resolved/aspire' }));
    });

    test('local settings use the selected folder while global settings stay window scoped', async () => {
        const folder = createWorkspaceFolder('a', '/repo/a');
        const target = workspaceFolderCliPathTarget(folder);
        workspaceFoldersStub.value([folder]);
        const localSettingsPath = path.join(tempDir, 'local.json');
        const globalSettingsPath = path.join(tempDir, 'global.json');
        fs.writeFileSync(localSettingsPath, '{}');
        fs.writeFileSync(globalSettingsPath, '{}');
        const getConfigInfoStub = sandbox.stub(ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            localSettingsPath,
            globalSettingsPath,
        } as any);
        sandbox.stub(vscode.workspace, 'openTextDocument').resolves({} as vscode.TextDocument);
        sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);

        await callbacks.get('aspire-vscode.openLocalSettings')!();
        await callbacks.get('aspire-vscode.openGlobalSettings')!();

        assert.ok(getConfigInfoStub.firstCall.calledWith({ target, cliPath: '/resolved/aspire' }));
        assert.ok(getConfigInfoStub.secondCall.calledWith({ target: windowCliPathTarget }));
    });

    test('update self remains window scoped without a CLI availability gate', async () => {
        workspaceFoldersStub.value([createWorkspaceFolder('a', '/repo/a')]);

        await callbacks.get('aspire-vscode.updateSelf')!();

        assert.strictEqual(resolveCliPathStub.called, false);
        assert.ok(sendCommandStub.calledOnceWith('update --self', true, undefined, { target: windowCliPathTarget }));
    });
});