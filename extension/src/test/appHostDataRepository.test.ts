import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../debugger/languages/cli';

class TestChildProcess extends EventEmitter {
    stdout = new PassThrough();
    stderr = new PassThrough();
    killed = false;

    kill(): boolean {
        this.killed = true;
        this.emit('close', null);
        return true;
    }
}

suite('AppHostDataRepository', () => {
    let terminalProvider: AspireTerminalProvider;
    let subscriptions: vscode.Disposable[];
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        subscriptions.forEach(subscription => subscription.dispose());
    });

    test('activate does not start describe watch while panel is hidden', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        await waitForMicrotasks();

        assert.strictEqual(getCliPathStub.called, false);
        assert.strictEqual(spawnStub.called, false);

        repository.dispose();
    });

    test('visible workspace panel starts describe watch', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.strictEqual(getCliPathStub.calledOnce, true);
        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json']);

        repository.dispose();
    });

    test('describe watch reports minimum CLI version when command help is returned', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const lineCallback = spawnStub.firstCall.args[3].lineCallback;
        const exitCallback = spawnStub.firstCall.args[3].exitCallback;
        lineCallback('Description:');
        lineCallback('Usage:');
        lineCallback('aspire [command] [options]');
        lineCallback('Commands:');
        exitCallback(1);

        assert.strictEqual(repository.hasError, true);
        assert.ok(repository.errorMessage?.includes('Aspire CLI 13.2.0'), repository.errorMessage);

        repository.dispose();
    });

    test('describe watch reports minimum AppHost version when workspace AppHost returns no data', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = options?.lineCallback;
            return new TestChildProcess();
        });
        spawnStub.onSecondCall().returns(new TestChildProcess());
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForMicrotasks();

            const exitCallback = spawnStub.secondCall.args[3].exitCallback;
            exitCallback(0);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire.Hosting 13.2.0'), repository.errorMessage);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('describe watch reports minimum AppHost version when workspace AppHost exits without unsupported command output', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = options?.lineCallback;
            return new TestChildProcess();
        });
        spawnStub.onSecondCall().returns(new TestChildProcess());
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForMicrotasks();

            const exitCallback = spawnStub.secondCall.args[3].exitCallback;
            exitCallback(1);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire.Hosting 13.2.0'), repository.errorMessage);
            assert.ok(!repository.errorMessage?.includes('Aspire CLI 13.2.0'), repository.errorMessage);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('describe watch clears compatibility error after receiving resource data', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const lineCallback = spawnStub.firstCall.args[3].lineCallback;
        lineCallback(JSON.stringify({ name: 'api' }));

        assert.strictEqual(repository.hasError, false);
        assert.strictEqual(repository.workspaceResources.length, 1);

        repository.dispose();
    });

    test('visible panel switches to global polling when workspace has multiple AppHosts and none is selected', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = options?.lineCallback;
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(describeProcess);
        spawnStub.onThirdCall().returns(psProcess);
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: null,
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForMicrotasks();

            assert.strictEqual(repository.viewMode, 'global');
            assert.strictEqual(describeProcess.killed, true);
            assert.strictEqual(spawnStub.callCount, 3);
            assert.deepStrictEqual(spawnStub.thirdCall.args[2], ['ps', '--format', 'json', '--resources']);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('visible panel switches to global polling when workspace has multiple AppHosts and one is selected', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = options?.lineCallback;
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(describeProcess);
        spawnStub.onThirdCall().returns(psProcess);
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForMicrotasks();

            assert.strictEqual(repository.viewMode, 'global');
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'apps/Store/AppHost.csproj');
            assert.strictEqual(describeProcess.killed, true);
            assert.strictEqual(spawnStub.callCount, 3);
            assert.deepStrictEqual(spawnStub.thirdCall.args[2], ['ps', '--format', 'json', '--resources']);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('single workspace AppHost candidate keeps workspace mode', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = options?.lineCallback;
            return new TestChildProcess();
        });
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            await waitForMicrotasks();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: null,
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'AppHost.csproj');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('visible workspace panel before activation starts describe watch once', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.setPanelVisible(true);
        repository.activate();
        await waitForMicrotasks();

        assert.strictEqual(getCliPathStub.calledOnce, true);
        assert.strictEqual(spawnStub.calledOnce, true);

        repository.dispose();
    });

    test('hiding workspace panel stops describe watch', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setPanelVisible(false);

        assert.strictEqual(childProcess.killed, true);

        repository.dispose();
    });

    test('hiding workspace panel clears workspace resources', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const lineCallback = spawnStub.firstCall.args[3].lineCallback;
        lineCallback(JSON.stringify({ name: 'api' }));

        assert.strictEqual(repository.workspaceResources.length, 1);

        repository.setPanelVisible(false);

        assert.strictEqual(repository.workspaceResources.length, 0);

        repository.dispose();
    });

    test('hiding workspace panel before cli path resolves prevents describe watch from starting', async () => {
        const cliPath = createDeferred<string>();
        getCliPathStub.returns(cliPath.promise);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        repository.setPanelVisible(false);
        cliPath.resolve('aspire');
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.called, false);

        repository.dispose();
    });

    test('late close from stopped describe watch does not orphan replacement watch', async () => {
        const firstChildProcess = new TestChildProcess();
        const secondChildProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(firstChildProcess);
        spawnStub.onSecondCall().returns(secondChildProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();
        const firstLineCallback = spawnStub.firstCall.args[3].lineCallback;
        const firstExitCallback = spawnStub.firstCall.args[3].exitCallback;

        repository.setPanelVisible(false);
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        firstLineCallback(JSON.stringify({ name: 'stale' }));
        firstExitCallback(0);
        repository.setPanelVisible(false);

        assert.strictEqual(repository.workspaceResources.length, 0);
        assert.strictEqual(firstChildProcess.killed, true);
        assert.strictEqual(secondChildProcess.killed, true);

        repository.dispose();
    });
});

suite('AppHostDataRepository AppHost-file gate', () => {
    let terminalProvider: AspireTerminalProvider;
    let subscriptions: vscode.Disposable[];
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        subscriptions.forEach(subscription => subscription.dispose());
    });

    test('opening AppHost file with hidden panel starts describe watch', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFileOpen(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json']);

        repository.dispose();
    });

    test('closing all AppHost files with hidden panel stops describe watch', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFileOpen(true);
        await waitForMicrotasks();

        repository.setAppHostFileOpen(false);

        assert.strictEqual(childProcess.killed, true);

        repository.dispose();
    });

    test('describe watch stays alive while either gate is open', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFileOpen(true);
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        // Closing the AppHost file should not stop the watch while the panel is still visible.
        repository.setAppHostFileOpen(false);
        assert.strictEqual(childProcess.killed, false);

        // Hiding the panel now stops it.
        repository.setPanelVisible(false);
        assert.strictEqual(childProcess.killed, true);

        repository.dispose();
    });

    test('redundant setAppHostFileOpen calls do not respawn describe', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFileOpen(true);
        repository.setAppHostFileOpen(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);

        repository.dispose();
    });
});

async function waitForMicrotasks(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
}

function createDeferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
    let resolve: (value: T) => void = () => { };
    const promise = new Promise<T>(promiseResolve => {
        resolve = promiseResolve;
    });
    return { promise, resolve };
}
