import * as assert from 'assert';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { EventEmitter } from 'events';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspirePackageRestoreProvider } from '../utils/AspirePackageRestoreProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import * as cliProcessModule from '../utils/process/cliProcess';

suite('AspirePackageRestoreProvider', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => sandbox.restore());

    test('resolves the CLI from the workspace folder that owns the config file', async () => {
        const folder = createWorkspaceFolder('/repo/workspace');
        const configUri = vscode.Uri.file(path.join(folder.uri.fsPath, 'aspire.config.json'));
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider({ getAspireCliExecutablePath } as unknown as AspireTerminalProvider);
        const childProcess = createChildProcess();
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            queueMicrotask(() => {
                options?.exitCallback?.(0);
                childProcess.emit('close', 0);
            });
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });

        try {
            await (provider as any)._runRestore(configUri, folder.uri.fsPath, 'aspire.config.json');

            assert.ok(getAspireCliExecutablePath.calledOnceWith(workspaceFolderCliPathTarget(folder)));
            assert.ok(spawnStub.calledOnceWith(
                provider['_terminalProvider'],
                '/repo/workspace/bin/aspire',
                ['restore'],
                sinon.match({ workingDirectory: folder.uri.fsPath })));
        } finally {
            provider.dispose();
        }
    });

    test('does not spawn restore when disposed during CLI resolution', async () => {
        let resolveCliPath!: (cliPath: string) => void;
        const cliPath = new Promise<string>(resolve => {
            resolveCliPath = resolve;
        });
        const getAspireCliExecutablePath = sandbox.stub().returns(cliPath);
        const provider = new AspirePackageRestoreProvider({ getAspireCliExecutablePath } as unknown as AspireTerminalProvider);
        const childProcess = createChildProcess();
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            queueMicrotask(() => {
                options?.exitCallback?.(0);
                childProcess.emit('close', 0);
            });
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });
        const configUri = vscode.Uri.file('/repo/workspace/aspire.config.json');

        const restore = (provider as any)._runRestore(configUri, '/repo/workspace', 'aspire.config.json') as Promise<void>;
        assert.ok(getAspireCliExecutablePath.calledOnce);
        provider.dispose();
        resolveCliPath('/repo/workspace/bin/aspire');
        await restore;

        assert.ok(spawnStub.notCalled);
    });
});

function createWorkspaceFolder(folderPath: string): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(folderPath),
        name: 'workspace',
        index: 0,
    };
}

function createChildProcess(): EventEmitter & { kill: sinon.SinonStub } {
    return Object.assign(new EventEmitter(), { kill: sinon.stub() });
}