import * as assert from 'assert';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { EventEmitter } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspirePackageRestoreProvider } from '../utils/AspirePackageRestoreProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { onDidResolveCliForOperation } from '../utils/cliOperationResolution';
import * as cliProcessModule from '../utils/process/cliProcess';
import * as workspaceModule from '../utils/workspace';

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
        const resolutions: string[] = [];
        const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution.cliPath));

        try {
            await (provider as any)._runRestore(configUri, folder.uri.fsPath, 'aspire.config.json', false);

            assert.ok(getAspireCliExecutablePath.calledOnceWith(workspaceFolderCliPathTarget(folder)));
            assert.ok(spawnStub.calledOnceWith(
                provider['_terminalProvider'],
                '/repo/workspace/bin/aspire',
                ['restore'],
                sinon.match({ workingDirectory: folder.uri.fsPath })));
            assert.deepStrictEqual(resolutions, []);
        } finally {
            subscription.dispose();
            provider.dispose();
        }
    });

    test('reports the exact CLI selected for a manual restore', async () => {
        const folder = createWorkspaceFolder('/repo/workspace');
        const configUri = vscode.Uri.file(path.join(folder.uri.fsPath, 'aspire.config.json'));
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider({ getAspireCliExecutablePath } as unknown as AspireTerminalProvider);
        const childProcess = createChildProcess();
        sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            queueMicrotask(() => {
                options?.exitCallback?.(0);
                childProcess.emit('close', 0);
            });
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });
        const resolutions: Array<{ target: unknown; cliPath: string }> = [];
        const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution));

        try {
            await (provider as any)._runRestore(configUri, folder.uri.fsPath, 'aspire.config.json', true);

            assert.deepStrictEqual(resolutions, [{
                target: workspaceFolderCliPathTarget(folder),
                cliPath: '/repo/workspace/bin/aspire',
            }]);
        } finally {
            subscription.dispose();
            provider.dispose();
        }
    });

    test('runs and reports a manual restore when auto-restore is disabled', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-manual-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = vscode.Uri.file(path.join(folder.uri.fsPath, 'aspire.config.json'));
        fs.writeFileSync(configUri.fsPath, '{}');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => false as T,
        } as unknown as vscode.WorkspaceConfiguration);
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
        const resolutions: string[] = [];
        const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution.cliPath));

        try {
            await (provider as any)._restoreIfChanged(configUri, true, true);

            assert.strictEqual(spawnStub.callCount, 1);
            assert.deepStrictEqual(resolutions, ['/repo/workspace/bin/aspire']);
        } finally {
            subscription.dispose();
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('preserves a manual restore queued behind an active automatic restore', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-queued-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = vscode.Uri.file(path.join(folder.uri.fsPath, 'aspire.config.json'));
        fs.writeFileSync(configUri.fsPath, '{}');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        sandbox.stub(workspaceModule, 'findAspireSettingsFiles').resolves([configUri]);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider({ getAspireCliExecutablePath } as unknown as AspireTerminalProvider);
        const childProcesses = [createChildProcess(), createChildProcess()];
        const restoreCompletions: Array<() => void> = [];
        let signalFirstSpawn!: () => void;
        let signalSecondSpawn!: () => void;
        const firstSpawned = new Promise<void>(resolve => signalFirstSpawn = resolve);
        const secondSpawned = new Promise<void>(resolve => signalSecondSpawn = resolve);
        let spawnIndex = 0;
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            const childProcess = childProcesses.shift()!;
            restoreCompletions.push(() => {
                options?.exitCallback?.(0);
                childProcess.emit('close', 0);
            });
            (spawnIndex++ === 0 ? signalFirstSpawn : signalSecondSpawn)();
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });
        const resolutions: string[] = [];
        const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution.cliPath));
        let signalQueued!: () => void;
        const queued = new Promise<void>(resolve => signalQueued = resolve);
        const pendingRestore = provider['_pendingRestore'];
        const setPendingRestore = pendingRestore.set.bind(pendingRestore);
        sandbox.stub(pendingRestore, 'set').callsFake((key, value) => {
            const result = setPendingRestore(key, value);
            signalQueued();
            return result;
        });

        try {
            const automaticRestore = (provider as any)._restoreIfChanged(configUri, true, false) as Promise<void>;
            await firstSpawned;
            const manualRestore = provider.retryRestore();
            await queued;

            restoreCompletions.shift()?.();
            await secondSpawned;
            restoreCompletions.shift()?.();
            await Promise.all([automaticRestore, manualRestore]);

            assert.strictEqual(spawnStub.callCount, 2);
            assert.deepStrictEqual(resolutions, ['/repo/workspace/bin/aspire']);
            assert.strictEqual(provider['_completed'], 2);
            assert.strictEqual(provider['_total'], 2);
        } finally {
            subscription.dispose();
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
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

        const restore = (provider as any)._runRestore(
            configUri,
            '/repo/workspace',
            'aspire.config.json',
            false) as Promise<void>;
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