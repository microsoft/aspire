/// <reference types="mocha" />

import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { registerCodeLensCommands } from '../activation/registerCodeLensCommands';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { createWorkspaceFolder } from './testHelpers';

suite('registerCodeLensCommands', () => {
    test('log commands use the workspace folder that owns the AppHost path', async () => {
        const sandbox = sinon.createSandbox();
        const callbacks = new Map<string, (...args: unknown[]) => Promise<unknown>>();
        sandbox.stub(vscode.commands, 'registerCommand').callsFake((command, callback) => {
            callbacks.set(command, callback as (...args: unknown[]) => Promise<unknown>);
            return { dispose: () => { } };
        });
        sandbox.stub(vscode.languages, 'registerCodeLensProvider').returns({ dispose: () => { } });
        const folder = createWorkspaceFolder('a', '/repo/a');
        const target = workspaceFolderCliPathTarget(folder);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(folder);
        const sendCommandStub = sinon.stub().resolves();
        const terminalProvider = {
            sendAspireCommandToAspireTerminal: sendCommandStub,
        } as unknown as AspireTerminalProvider;
        const treeProvider = {
            onDidChangeTreeData: () => ({ dispose: () => { } }),
        } as unknown as AspireAppHostTreeProvider;
        const registrations = registerCodeLensCommands(
            treeProvider,
            {} as vscode.TreeView<unknown>,
            {
                viewMode: 'global',
                // registerCodeLensCommands now wires the debugger install hint watcher to this
                // repository, and the watcher refreshes immediately, so the fake has to satisfy the
                // whole DebuggerInstallHintDataSource shape. Empty collections mean no AppHost is
                // known, so no data lease is taken and no install hints fire during this test.
                workspaceAppHostCandidatePaths: [],
                workspaceResources: [],
                appHosts: [],
                onDidChangeData: () => ({ dispose: () => { } }),
                keepDataActive: () => ({ dispose: () => { } }),
            } as unknown as AppHostDataRepository,
            terminalProvider,
            {} as AspireEditorCommandProvider,
            { get: () => undefined, update: async () => { } } as unknown as vscode.Memento,
        );
        const appHostPath = '/repo/a/AppHost/AppHost.csproj';

        try {
            await callbacks.get('aspire-vscode.codeLensViewLogs')!('api', appHostPath);
            await callbacks.get('aspire-vscode.codeLensViewAppHostLogs')!(appHostPath);

            assert.deepStrictEqual(sendCommandStub.firstCall.args[3], { target });
            assert.deepStrictEqual(sendCommandStub.secondCall.args[3], { target });
        }
        finally {
            registrations.forEach(registration => registration.dispose());
            sandbox.restore();
        }
    });
});