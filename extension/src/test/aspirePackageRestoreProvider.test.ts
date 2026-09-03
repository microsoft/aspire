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
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import type { CapabilityStatus } from '../types/configInfo';

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
            await (provider as any)._restoreIfNeeded(configUri, true);

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
        const configUri = createGuestConfig(directory, '13.6.0+old-commit');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        sandbox.stub(workspaceModule, 'findAspireSettingsFiles').resolves([configUri]);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0+new-commit'));
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
            const automaticRestore = (provider as any)._restoreIfNeeded(configUri, false) as Promise<void>;
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

    test('skips automatic restore when generated modules match the selected CLI version', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-current-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = createGuestConfig(directory, '13.6.0+same-commit');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0+same-commit'));
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess');

        try {
            await (provider as any)._restoreIfNeeded(configUri, false);

            assert.ok(spawnStub.notCalled);
        } finally {
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('runs automatic restore once when existing generated modules have no version marker', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-unmarked-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = createGuestConfig(directory, null);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0'));
        const childProcess = createChildProcess();
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            queueMicrotask(() => {
                options?.exitCallback?.(0);
                childProcess.emit('close', 0);
            });
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });

        try {
            await (provider as any)._restoreIfNeeded(configUri, false);

            assert.strictEqual(spawnStub.callCount, 1);
            assert.ok(getAspireCliExecutablePath.calledOnce);
        } finally {
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('uses the modern generated modules when legacy and modern TypeScript AppHosts coexist', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-migrating-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = vscode.Uri.file(path.join(directory, 'aspire.config.json'));
        fs.writeFileSync(configUri.fsPath, JSON.stringify({
            appHost: {
                path: 'apphost.ts',
                language: 'typescript/nodejs',
            },
        }));
        fs.writeFileSync(path.join(directory, 'apphost.ts'), '// legacy');
        fs.writeFileSync(path.join(directory, 'apphost.mts'), '// modern');
        fs.mkdirSync(path.join(directory, '.modules'), { recursive: true });
        fs.writeFileSync(path.join(directory, '.modules', '.codegen-version'), '13.5.0');
        fs.mkdirSync(path.join(directory, '.aspire', 'modules'), { recursive: true });
        fs.writeFileSync(path.join(directory, '.aspire', 'modules', '.codegen-version'), '13.6.0');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0'));
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess');

        try {
            await (provider as any)._restoreIfNeeded(configUri, false);

            assert.ok(spawnStub.notCalled);
        } finally {
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('skips automatic restore for missing modules and CLIs without version markers', async () => {
        const missingDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-missing-modules-'));
        const unsupportedDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-unsupported-marker-'));
        const missingFolder = createWorkspaceFolder(missingDirectory);
        const unsupportedFolder = createWorkspaceFolder(unsupportedDirectory);
        const missingConfigUri = createGuestConfig(missingDirectory);
        const unsupportedConfigUri = createGuestConfig(unsupportedDirectory, '13.5.0');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder')
            .withArgs(missingConfigUri).returns(missingFolder)
            .withArgs(unsupportedConfigUri).returns(unsupportedFolder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('unsupported', '13.6.0'));
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess');

        try {
            await (provider as any)._restoreIfNeeded(missingConfigUri, false);
            await (provider as any)._restoreIfNeeded(unsupportedConfigUri, false);

            assert.ok(spawnStub.notCalled);
            assert.strictEqual(getAspireCliExecutablePath.callCount, 1);
        } finally {
            provider.dispose();
            fs.rmSync(missingDirectory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
            fs.rmSync(unsupportedDirectory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('skips automatic restore for .NET AppHosts', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-dotnet-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = vscode.Uri.file(path.join(directory, 'aspire.config.json'));
        fs.writeFileSync(configUri.fsPath, JSON.stringify({ appHost: { path: 'AppHost.fsproj' } }));
        fs.mkdirSync(path.join(directory, '.aspire', 'modules'), { recursive: true });
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0'));
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess');

        try {
            await (provider as any)._restoreIfNeeded(configUri, false);

            assert.ok(spawnStub.notCalled);
            assert.ok(getAspireCliExecutablePath.notCalled);
        } finally {
            provider.dispose();
            fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        }
    });

    test('skips automatic restore in an untrusted workspace', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-untrusted-restore-'));
        const folder = createWorkspaceFolder(directory);
        const configUri = createGuestConfig(directory, '13.5.0');
        sandbox.stub(vscode.workspace, 'isTrusted').value(false);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(configUri).returns(folder);
        sandbox.stub(vscode.workspace, 'getConfiguration').returns({
            get: <T>() => true as T,
        } as unknown as vscode.WorkspaceConfiguration);
        const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
        const provider = new AspirePackageRestoreProvider(
            { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
            createConfigInfoProvider('supported', '13.6.0'));
        const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess');

        try {
            await (provider as any)._restoreIfNeeded(configUri, false);

            assert.ok(spawnStub.notCalled);
            assert.ok(getAspireCliExecutablePath.notCalled);
        } finally {
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

function createGuestConfig(directory: string, generatedVersion?: string | null): vscode.Uri {
    const configUri = vscode.Uri.file(path.join(directory, 'aspire.config.json'));
    fs.writeFileSync(configUri.fsPath, JSON.stringify({
        appHost: {
            path: 'apphost.mts',
            language: 'typescript/nodejs',
        },
    }));

    if (generatedVersion !== undefined) {
        const modulesDirectory = path.join(directory, '.aspire', 'modules');
        fs.mkdirSync(modulesDirectory, { recursive: true });
        if (generatedVersion !== null) {
            fs.writeFileSync(path.join(modulesDirectory, '.codegen-version'), generatedVersion);
        }
    }

    return configUri;
}

function createConfigInfoProvider(status: CapabilityStatus, version: string): ConfigInfoProvider {
    return {
        getCapabilityStatus: sinon.stub().resolves(status),
        getCliVersion: sinon.stub().resolves({
            cliPath: '/repo/workspace/bin/aspire',
            version,
            executableIdentity: 'test-cli',
        }),
    } as unknown as ConfigInfoProvider;
}