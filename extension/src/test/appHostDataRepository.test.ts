import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import type { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import * as cliModule from '../debugger/languages/cli';

class TestChildProcess extends EventEmitter {
    stdout = new PassThrough();
    stderr = new PassThrough();
    killed = false;
    exitCode: number | null = null;
    signalCode: NodeJS.Signals | null = null;
    killSignals: Array<NodeJS.Signals | number | undefined> = [];

    constructor(private readonly _closeOnKill = true) {
        super();
    }

    kill(signal?: NodeJS.Signals | number): boolean {
        this.killed = true;
        this.killSignals.push(signal);
        if (this._closeOnKill) {
            this.exitCode = 0;
            this.emit('close', null);
        }
        return true;
    }

    markExited(exitCode = 0): void {
        this.exitCode = exitCode;
    }
}

function createLsLineCallback(options: any): (line: string) => void {
    return line => {
        options?.stdoutCallback?.(line);
        options?.exitCallback?.(0);
    };
}

suite('AppHostDataRepository', () => {
    let terminalProvider: AspireTerminalProvider;
    let subscriptions: vscode.Disposable[];
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;
    let defaultWorkspaceFoldersStub: sinon.SinonStub;
    let findFilesStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
        defaultWorkspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        findFilesStub = sinon.stub(vscode.workspace, 'findFiles').resolves([]);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        findFilesStub.restore();
        if (defaultWorkspaceFoldersStub.restore) {
            defaultWorkspaceFoldersStub.restore();
        }
        subscriptions.forEach(subscription => subscription.dispose());
    });

    function stubWorkspaceFolders(workspaceFolders: readonly vscode.WorkspaceFolder[]): sinon.SinonStub {
        defaultWorkspaceFoldersStub.restore();
        defaultWorkspaceFoldersStub = { restore: () => { } } as sinon.SinonStub;
        return sinon.stub(vscode.workspace, 'workspaceFolders').value(workspaceFolders);
    }

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

    test('describe watch does not report compatibility error when workspace AppHost returns no data successfully', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        spawnStub.onSecondCall().returns(new TestChildProcess());
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            const exitCallback = spawnStub.secondCall.args[3].exitCallback;
            exitCallback(0);

            assert.strictEqual(repository.hasError, false);
            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('describe watch reports minimum AppHost version when workspace AppHost exits without unsupported command output', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        spawnStub.onSecondCall().returns(new TestChildProcess());
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

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

    test('workspace ps success does not clear describe error', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(psProcess);
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            const describeErrorCallback = spawnStub.secondCall.args[3].errorCallback;
            describeErrorCallback(new Error('describe failed'));
            assert.ok(repository.errorMessage?.includes('describe failed'), repository.errorMessage);

            const psOptions = spawnStub.thirdCall.args[3];
            psOptions.lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1234,
                cliPid: null,
                dashboardUrl: null,
            }]));

            assert.ok(repository.errorMessage?.includes('describe failed'), repository.errorMessage);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps success clears previous ps error', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        const psFollowProcess = new TestChildProcess();
        const psFallbackProcess = new TestChildProcess();
        const replacementDescribeProcess = new TestChildProcess();
        const psSuccessProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(describeProcess);
        spawnStub.onThirdCall().returns(psFollowProcess);
        spawnStub.onCall(3).returns(psFallbackProcess);
        spawnStub.onCall(4).returns(replacementDescribeProcess);
        spawnStub.onCall(5).returns(psSuccessProcess);
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            const psFollowOptions = spawnStub.thirdCall.args[3];
            psFollowOptions.exitCallback(1);
            await waitForAppHostDiscovery();

            const psFallbackOptions = spawnStub.getCall(3).args[3];
            psFallbackOptions.stderrCallback('ps failed');
            psFallbackOptions.exitCallback(1);
            assert.ok(repository.errorMessage?.includes('ps failed'), repository.errorMessage);

            repository.setPanelVisible(false);
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            await waitForAppHostDiscovery();

            const psSuccessCall = spawnStub.getCalls().filter(call => call.args[2][0] === 'ps').at(-1);
            assert.ok(psSuccessCall);
            const psSuccessOptions = psSuccessCall.args[3];
            psSuccessOptions.stdoutCallback('[]');
            psSuccessOptions.exitCallback(0);

            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('visible panel keeps workspace view when workspace has multiple AppHosts and none is selected', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(describeProcess);
        spawnStub.onThirdCall().returns(psProcess);
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: null,
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(spawnStub.callCount, 2);
            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['ps', '--follow', '--format', 'json']);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('visible panel keeps workspace view when workspace has multiple AppHosts and one is selected', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return getAppHostsProcess;
        });
        spawnStub.onSecondCall().returns(describeProcess);
        spawnStub.onThirdCall().returns(psProcess);
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'apps/Store/AppHost.csproj');
            assert.strictEqual(describeProcess.killed, false);
            assert.strictEqual(spawnStub.callCount, 3);
            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/apps/Store/AppHost.csproj']);
            assert.deepStrictEqual(spawnStub.thirdCall.args[2], ['ps', '--follow', '--format', 'json']);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('multi-AppHost workspace retargets describe to the only running AppHost', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcesses: TestChildProcess[] = [];
        const describeCalls: { args: string[]; options: any }[] = [];
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeCalls.push({ args, options });
                const process = new TestChildProcess();
                describeProcesses.push(process);
                return process;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(describeCalls.length, 1);
            assert.deepStrictEqual(describeCalls[0].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/apps/Store/AppHost.csproj']);
            assert.ok(psOptions);

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/samples/Store/AppHost.csproj',
                    appHostPid: 125881,
                    cliPid: 125738,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/samples/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'samples/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(describeProcesses[0].killed, true);
            assert.strictEqual(describeCalls.length, 2);
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/samples/Store/AppHost.csproj']);

            describeCalls[1].options.lineCallback(JSON.stringify({ name: 'api', resourceType: 'Project', state: 'Running' }));
            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceResources[0].name, 'api');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('multi-AppHost workspace does not retarget describe when multiple candidate AppHosts are running', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcesses: TestChildProcess[] = [];
        const describeCalls: { args: string[]; options: any }[] = [];
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeCalls.push({ args, options });
                const process = new TestChildProcess();
                describeProcesses.push(process);
                return process;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                    '/workspace/tools/Admin/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(describeCalls.length, 1);
            assert.deepStrictEqual(describeCalls[0].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/apps/Store/AppHost.csproj']);
            assert.ok(psOptions);

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/samples/Store/AppHost.csproj',
                    appHostPid: 125881,
                    cliPid: 125738,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
                {
                    appHostPath: '/workspace/tools/Admin/AppHost.csproj',
                    appHostPid: 125882,
                    cliPid: 125739,
                    dashboardUrl: 'https://localhost:17194/login?t=061213',
                },
            ]));
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost, undefined);
            // No retarget, but global describe streams start for the non-selected running AppHosts
            // so their resources appear in the workspace tree.
            assert.strictEqual(describeCalls.length, 3);
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/samples/Store/AppHost.csproj']);
            assert.deepStrictEqual(describeCalls[2].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/tools/Admin/AppHost.csproj']);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('non-selected running AppHosts in workspace get resources from per-AppHost describe streams', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcesses: TestChildProcess[] = [];
        const describeCalls: { args: string[]; options: any }[] = [];
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider: any, _command: any, args: string[], options: any) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeCalls.push({ args, options });
                const process = new TestChildProcess();
                describeProcesses.push(process);
                return process;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();
            assert.strictEqual(describeCalls.length, 1);
            assert.ok(psOptions);

            // Simulate both AppHosts running
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125880,
                    cliPid: 125737,
                    dashboardUrl: 'https://localhost:17192/login?t=061211',
                },
                {
                    appHostPath: '/workspace/samples/Store/AppHost.csproj',
                    appHostPid: 125881,
                    cliPid: 125738,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));
            await waitForMicrotasks();

            // Global describe for non-selected AppHost spawns asynchronously after resolving CLI path
            await waitForCondition(() => describeCalls.length >= 2, 'global describe for non-selected AppHost should start');

            // Initial workspace describe + global describe for the non-selected AppHost
            // (workspace describe restart is still pending on a timer)
            assert.strictEqual(describeCalls.length, 2);
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/samples/Store/AppHost.csproj']);

            // Simulate resource data arriving on the non-selected AppHost's describe stream (NDJSON format)
            describeCalls[1].options.lineCallback(JSON.stringify({ name: 'redis', resourceType: 'Container', state: 'Running' }));
            await waitForMicrotasks();

            // The non-selected AppHost should have its resources populated
            const nonSelectedAppHost = repository.appHosts.find((a: any) => a.appHostPath === '/workspace/samples/Store/AppHost.csproj');
            assert.ok(nonSelectedAppHost);
            assert.ok(nonSelectedAppHost.resources);
            assert.strictEqual(nonSelectedAppHost.resources!.length, 1);
            assert.strictEqual(nonSelectedAppHost.resources![0].name, 'redis');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('configured AppHost outside aspire ls candidates remains selected in workspace view', async () => {
        const workspaceRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-extension-workspace-'));
        const configuredAppHostPath = path.join(path.dirname(workspaceRoot), 'external', 'AppHost.csproj');
        const discoveredAppHostPath = path.join(workspaceRoot, 'apps', 'Store', 'AppHost.csproj');
        const secondDiscoveredAppHostPath = path.join(workspaceRoot, 'samples', 'Store', 'AppHost.csproj');
        let workspaceFoldersStub: sinon.SinonStub | undefined;
        let repository: AppHostDataRepository | undefined;

        try {
            fs.writeFileSync(path.join(workspaceRoot, 'aspire.config.json'), JSON.stringify({
                appHost: {
                    path: configuredAppHostPath,
                },
            }));
            findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                const pattern = typeof include === 'string' ? include : include.pattern;
                return pattern.endsWith('aspire.config.json')
                    ? [vscode.Uri.file(path.join(workspaceRoot, 'aspire.config.json'))]
                    : [];
            });

            let getAppHostsLineCallback: ((line: string) => void) | undefined;
            let psOptions: any;
            spawnStub.callsFake((_terminalProvider, _command, args, options) => {
                if (args[0] === 'ls') {
                    getAppHostsLineCallback = createLsLineCallback(options);
                }
                if (args[0] === 'ps') {
                    psOptions = options;
                }
                return new TestChildProcess();
            });
            workspaceFoldersStub = stubWorkspaceFolders([{
                uri: vscode.Uri.file(workspaceRoot),
                name: 'workspace',
                index: 0,
            }]);
            repository = new AppHostDataRepository(terminalProvider);

            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify([
                {
                    relativePath: 'apps/Store/AppHost.csproj',
                    path: discoveredAppHostPath,
                    language: 'csharp',
                    status: 'buildable',
                },
                {
                    relativePath: 'samples/Store/AppHost.csproj',
                    path: secondDiscoveredAppHostPath,
                    language: 'csharp',
                    status: 'buildable',
                },
            ]));
            await waitForCondition(
                () => repository?.workspaceAppHostPath === configuredAppHostPath && spawnStub.callCount >= 2 && psOptions !== undefined,
                'configured AppHost discovery did not finish');

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, configuredAppHostPath);
            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['describe', '--follow', '--format', 'json', '--apphost', configuredAppHostPath]);

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: configuredAppHostPath,
                    appHostPid: 125881,
                },
            ]));
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, configuredAppHostPath);
        } finally {
            repository?.dispose();
            workspaceFoldersStub?.restore();
            fs.rmSync(workspaceRoot, { recursive: true, force: true });
        }
    });

    test('single workspace AppHost candidate keeps workspace mode', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        const workspaceFoldersStub = stubWorkspaceFolders([{
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
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostDescription, 'Workspace view selected because aspire ls found one buildable AppHost.');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('possibly unbuildable AppHost candidates do not force global mode', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);
            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ls', '--format', 'json']);

            getAppHostsLineCallback(JSON.stringify([
                {
                    relativePath: 'apps/Store/AppHost.csproj',
                    path: '/workspace/apps/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                },
                {
                    relativePath: 'samples/Store/AppHost.csproj',
                    path: '/workspace/samples/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'possibly-unbuildable',
                },
            ]));
            // aspire ls exit handler awaits getConfiguredAppHostPathFromWorkspaceRoot, which
            // probes for aspire.config.json / .aspire/settings.json via vscode workspace fs.
            // That probe can take more than one macrotask on Windows, so poll for completion
            // instead of relying on a single setTimeout(0) tick.
            await waitForCondition(
                () => repository.workspaceAppHostPath === '/workspace/apps/Store/AppHost.csproj',
                'buildable AppHost discovery did not finish');

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHostName, 'AppHost.csproj');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace discovery with no AppHost candidates clears loading context', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify([]));
            await waitForAppHostDiscovery();

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            const noAppHostContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noAppHostContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace discovery with only non-buildable AppHost candidates clears loading context', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify([
                {
                    relativePath: 'apps/Store/Store.csproj',
                    path: '/workspace/apps/Store/Store.csproj',
                    language: 'csharp',
                    status: 'possibly-unbuildable',
                },
            ]));
            await waitForAppHostDiscovery();

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            const noAppHostContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noAppHostContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace discovery with no buildable AppHosts clears stale running state', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const candidates = [
            {
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            },
        ];
        const discoveryChanges = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: discoveryChanges.event,
            discover: async () => candidates,
            dispose: () => { },
        };
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => psOptions !== undefined, 'ps watch did not start');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                },
            ]));
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);

            candidates.splice(0, candidates.length, {
                path: '/workspace/apps/Store/Store.csproj',
                language: 'csharp',
                status: 'possibly-unbuildable',
            });
            discoveryChanges.fire(workspaceFolder);
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.appHosts.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
            const noAppHostContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noAppHostContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            discoveryChanges.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps failure clears loading context and shows error welcome', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return new TestChildProcess();
        });
        spawnStub.onSecondCall().returns(new TestChildProcess());
        spawnStub.onThirdCall().returns(new TestChildProcess());
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: null,
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
                app_host_candidates: [
                    {
                        relativePath: 'apps/Store/AppHost.csproj',
                        path: '/workspace/apps/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        relativePath: 'samples/Store/AppHost.csproj',
                        path: '/workspace/samples/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                ],
            }));
            await waitForAppHostDiscovery();

            const psFollowOptions = spawnStub.secondCall.args[3];
            psFollowOptions.exitCallback(1);
            await waitForAppHostDiscovery();

            const psFallbackOptions = spawnStub.thirdCall.args[3];
            psFallbackOptions.stderrCallback('ps failed');
            psFallbackOptions.exitCallback(1);

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            const errorContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsError');
            assert.strictEqual(errorContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps CLI path failure clears loading context and shows error welcome', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => [
                {
                    path: '/workspace/apps/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                },
                {
                    path: '/workspace/samples/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                },
            ],
        };
        getCliPathStub.rejects(new Error('CLI missing'));
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            const errorContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsError');
            assert.strictEqual(errorContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace AppHost discovery failure clears loading context and shows error welcome', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => {
                throw new Error('aspire ls failed');
            },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            const errorContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsError');
            assert.strictEqual(errorContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
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

    test('visible workspace panel tracks running AppHost with no resources from ps', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let psArgs: string[] | undefined;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'ps') {
                psArgs = args;
                psOptions = options;
            }
            return new TestChildProcess();
        });

        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.ok(getAppHostsLineCallback);
            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apphost/apphost.cs',
                all_project_file_candidates: ['/workspace/apphost/apphost.cs'],
            }));
            await waitForAppHostDiscovery();

            assert.ok(psOptions);
            assert.deepStrictEqual(psArgs, ['ps', '--follow', '--format', 'json']);
            psOptions.lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apphost/apphost.cs',
                appHostPid: 125881,
                cliPid: 125738,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(repository.workspaceAppHost?.cliPid, 125738);
            assert.strictEqual(repository.workspaceAppHost?.dashboardUrl, 'https://localhost:17193/login?t=061212');

            repository.setPanelVisible(false);

            assert.strictEqual(repository.workspaceAppHost, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('multi-AppHost workspace ps snapshot clears no running AppHosts context', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });

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
                app_host_candidates: [
                    {
                        relativePath: 'apps/Store/AppHost.csproj',
                        path: '/workspace/apps/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        relativePath: 'samples/Store/AppHost.csproj',
                        path: '/workspace/samples/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                ],
            }));
            await waitForAppHostDiscovery();

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    cliPid: 125738,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));

            const noRunningContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noRunningContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('multi-AppHost workspace empty ps snapshot clears loading context', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });

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
                app_host_candidates: [
                    {
                        relativePath: 'apps/Store/AppHost.csproj',
                        path: '/workspace/apps/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        relativePath: 'samples/Store/AppHost.csproj',
                        path: '/workspace/samples/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    },
                ],
            }));
            await waitForAppHostDiscovery();

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([]));

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);

            // noAppHosts is false because workspace candidates are still present (idle AppHosts)
            const noRunningContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noRunningContextCalls.at(-1)?.args[2], false);

            // noRunningAppHosts is true because aspire ps returned no running AppHosts.
            // This distinguishes "discovered candidates exist" from "any AppHost is actually
            // running" — the Open Dashboard palette entry should be hidden in this state
            // because no live dashboard URL is available.
            const noLiveAppHostsCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noRunningAppHosts');
            assert.strictEqual(noLiveAppHostsCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps snapshot clears stale describe resources when selected AppHost stops', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcess = new TestChildProcess();
        let describeOptions: any;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions = options;
                return describeProcess;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });

        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.ok(getAppHostsLineCallback);
            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/labs/ops/apphost.cs',
                all_project_file_candidates: ['/workspace/labs/ops/apphost.cs'],
            }));
            await waitForAppHostDiscovery();

            assert.ok(describeOptions);
            assert.ok(psOptions);
            describeOptions.lineCallback(JSON.stringify({ name: 'worker', resourceType: 'Project', state: 'Running' }));
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));

            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/labs/ops/apphost.cs');

            psOptions.lineCallback(JSON.stringify([]));

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
            assert.strictEqual(describeProcess.killed, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace describe exit clears stale running AppHost before ps stop snapshot', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcess = new TestChildProcess();
        let describeOptions: any;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions = options;
                return describeProcess;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });

        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.ok(getAppHostsLineCallback);
            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/labs/ops/apphost.cs',
                all_project_file_candidates: ['/workspace/labs/ops/apphost.cs'],
            }));
            await waitForAppHostDiscovery();

            assert.ok(describeOptions);
            assert.ok(psOptions);
            describeOptions.lineCallback(JSON.stringify({ name: 'worker', resourceType: 'Project', state: 'Running' }));
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));

            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(repository.appHosts.length, 1);

            describeOptions.exitCallback(0);

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
            assert.strictEqual(repository.appHosts.length, 0);

            // noAppHosts is false because workspace candidates are still present (idle AppHosts)
            const noRunningContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noRunningContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps start restarts describe after earlier empty describe exit', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let psOptions: any;
        const describeProcesses: TestChildProcess[] = [];
        const describeOptions: any[] = [];
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions.push(options);
                const process = new TestChildProcess();
                describeProcesses.push(process);
                return process;
            }
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });

        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.ok(getAppHostsLineCallback);
            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/labs/ops/apphost.cs',
                all_project_file_candidates: ['/workspace/labs/ops/apphost.cs'],
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(describeOptions.length, 1);
            describeOptions[0].exitCallback(0);
            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(Boolean(repository.workspaceAppHost), false);

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(describeOptions.length, 2);

            describeOptions[1].lineCallback(JSON.stringify({ name: 'worker', resourceType: 'Project', state: 'Running' }));
            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceResources[0].name, 'worker');
            assert.strictEqual(describeProcesses[0].killed, false);
            assert.strictEqual(describeProcesses[1].killed, false);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
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

    test('stubborn describe watch is force killed', async () => {
        const clock = sinon.useFakeTimers();
        const childProcess = new TestChildProcess(false);
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setPanelVisible(false);
            clock.tick(5000);

            assert.deepStrictEqual(childProcess.killSignals, [undefined, 'SIGKILL']);
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('already-exited describe watch is not terminated again', async () => {
        const childProcess = new TestChildProcess();
        childProcess.markExited();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setPanelVisible(false);

        assert.strictEqual(childProcess.killed, false);
        assert.strictEqual(childProcess.listenerCount('close'), 0);
        assert.strictEqual(childProcess.listenerCount('exit'), 0);

        repository.dispose();
    });
});

suite('AppHostDataRepository global polling', () => {
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

    test('hiding global panel kills in-flight ps process', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--follow', '--format', 'json']);

        repository.setPanelVisible(false);

        assert.strictEqual(childProcess.killed, true);

        repository.dispose();
    });

    test('global panel starts ps follow and updates from streamed AppHost deltas', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--follow', '--format', 'json']);

        const psLineCallback = spawnStub.firstCall.args[3].lineCallback;
        psLineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1234,
            status: 'running',
        }));
        await waitForMicrotasks();

        assert.strictEqual(repository.appHosts.length, 1);
        assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/AppHost.csproj');

        // The repository should now have spawned `aspire describe --follow --apphost <path>`
        // for the discovered AppHost so the global tree can show resources.
        const describeCall = spawnStub.getCalls().find(call =>
            Array.isArray(call.args[2]) && call.args[2][0] === 'describe' && call.args[2].includes('/workspace/AppHost.csproj'));
        assert.ok(describeCall, 'expected aspire describe --follow to spawn for the discovered AppHost');
        assert.deepStrictEqual(describeCall.args[2], ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/AppHost.csproj']);

        const describeLineCallback = describeCall.args[3].lineCallback;
        describeLineCallback(JSON.stringify({ name: 'api', resourceType: 'Project', state: 'Running' }));
        assert.strictEqual(repository.appHosts[0].resources?.[0].name, 'api');

        psLineCallback(JSON.stringify({
            appHostPath: '/workspace/OtherAppHost.csproj',
            appHostPid: 5678,
            status: 'running',
        }));
        await waitForMicrotasks();

        assert.strictEqual(repository.appHosts.length, 2);
        assert.strictEqual(repository.appHosts[1].appHostPath, '/workspace/OtherAppHost.csproj');
        assert.deepStrictEqual(repository.appHosts[1].resources, []);

        psLineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 9999,
            status: 'running',
        }));
        await waitForMicrotasks();

        assert.strictEqual(repository.appHosts.length, 3);
        assert.strictEqual(repository.appHosts[2].appHostPath, '/workspace/AppHost.csproj');
        assert.strictEqual(repository.appHosts[2].appHostPid, 9999);

        psLineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1234,
            status: 'stopped',
        }));
        await waitForMicrotasks();

        assert.strictEqual(repository.appHosts.length, 2);
        assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/OtherAppHost.csproj');
        assert.strictEqual(repository.appHosts[1].appHostPid, 9999);

        repository.dispose();
    });

    test('hiding global panel before cli path resolves prevents ps from starting', async () => {
        const cliPath = createDeferred<string>();
        getCliPathStub.returns(cliPath.promise);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        repository.setPanelVisible(false);
        cliPath.resolve('aspire');
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.called, false);

        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);

        repository.dispose();
    });

    test('cli path failure does not disable ps polling', async () => {
        const clock = sinon.useFakeTimers();
        getCliPathStub.onFirstCall().rejects(new Error('CLI path unavailable'));
        getCliPathStub.onSecondCall().resolves('aspire');
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.called, false);

            clock.tick(30000);
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.calledOnce, true);
            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--format', 'json']);
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('ps follow fallback starts only one polling interval when spawn reports error and close', async () => {
        const clock = sinon.useFakeTimers();
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.calledOnce, true);
            const psFollowOptions = spawnStub.firstCall.args[3];
            const timerCountBeforeFallback = clock.countTimers();
            psFollowOptions.errorCallback(new Error('spawn ENOENT'));
            psFollowOptions.exitCallback(-2);
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.calledTwice, true);
            assert.strictEqual(clock.countTimers(), timerCountBeforeFallback + 1);
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('stopped ps does not start fallback after exit', async () => {
        const childProcess = new TestChildProcess();
        spawnStub.returns(childProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();
        const exitCallback = spawnStub.firstCall.args[3].exitCallback;

        repository.setPanelVisible(false);
        exitCallback(1);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);

        repository.dispose();
    });

    test('dispose kills in-flight ps fallback process', async () => {
        const firstChildProcess = new TestChildProcess();
        const fallbackChildProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(firstChildProcess);
        spawnStub.onSecondCall().returns(fallbackChildProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();
        const exitCallback = spawnStub.firstCall.args[3].exitCallback;

        exitCallback(1);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledTwice, true);

        repository.dispose();

        assert.strictEqual(fallbackChildProcess.killed, true);
    });

    test('synchronously completed ps process is not tracked for later termination', async () => {
        let childProcess: TestChildProcess | undefined;
        spawnStub.callsFake((_terminalProvider, _cliPath, _args, options) => {
            childProcess = new TestChildProcess();
            options.exitCallback(0);
            return childProcess;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setPanelVisible(false);

        assert.ok(childProcess);
        assert.strictEqual(childProcess.killed, false);

        repository.dispose();
    });

    test('global mode spawns describe per AppHost and tears down on AppHost removal', async () => {
        const spawned: { args: string[]; process: TestChildProcess; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            const process = new TestChildProcess();
            spawned.push({ args, process, options });
            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const psCall = spawned.find(call => call.args[0] === 'ps');
        assert.ok(psCall);

        psCall.options.lineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1234,
            status: 'running',
        }));
        psCall.options.lineCallback(JSON.stringify({
            appHostPath: '/workspace/OtherAppHost.csproj',
            appHostPid: 5678,
            status: 'running',
        }));
        await waitForMicrotasks();

        const describeCalls = spawned.filter(call => call.args[0] === 'describe');
        assert.strictEqual(describeCalls.length, 2);
        const paths = describeCalls.map(call => call.args[call.args.indexOf('--apphost') + 1]).sort();
        assert.deepStrictEqual(paths, ['/workspace/AppHost.csproj', '/workspace/OtherAppHost.csproj']);

        const firstDescribe = describeCalls.find(call => call.args.includes('/workspace/AppHost.csproj'))!;
        firstDescribe.options.lineCallback(JSON.stringify({ name: 'api', resourceType: 'Project', state: 'Running' }));
        firstDescribe.options.lineCallback(JSON.stringify({ name: 'db', resourceType: 'Container', state: 'Running' }));

        const first = repository.appHosts.find(a => a.appHostPath === '/workspace/AppHost.csproj');
        assert.ok(first);
        assert.strictEqual(first.resources?.length, 2);
        assert.deepStrictEqual(first.resources?.map(r => r.name).sort(), ['api', 'db']);

        // Stop the first AppHost — its describe stream should be torn down.
        psCall.options.lineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1234,
            status: 'stopped',
        }));
        await waitForMicrotasks();

        assert.strictEqual(firstDescribe.process.killed, true);
        assert.strictEqual(repository.appHosts.length, 1);
        assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/OtherAppHost.csproj');

        repository.dispose();
    });

    test('global describe streams are stopped when switching to workspace mode', async () => {
        const spawned: { args: string[]; process: TestChildProcess; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            const process = new TestChildProcess();
            spawned.push({ args, process, options });
            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setViewMode('global');
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const psCall = spawned.find(call => call.args[0] === 'ps');
        assert.ok(psCall);
        psCall.options.lineCallback(JSON.stringify({
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1234,
            status: 'running',
        }));
        await waitForMicrotasks();

        const describeCall = spawned.find(call => call.args[0] === 'describe');
        assert.ok(describeCall);
        assert.strictEqual(describeCall.process.killed, false);

        repository.setViewMode('workspace');
        await waitForMicrotasks();

        assert.strictEqual(describeCall.process.killed, true);

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

async function waitForAppHostDiscovery(): Promise<void> {
    await waitForMicrotasks();
    await new Promise(resolve => setTimeout(resolve, 0));
    await waitForMicrotasks();
}

async function waitForCondition(condition: () => boolean, message: string): Promise<void> {
    for (let i = 0; i < 100; i++) {
        if (condition()) {
            return;
        }

        await waitForAppHostDiscovery();
    }

    assert.ok(condition(), message);
}

function createDeferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
    let resolve: (value: T) => void = () => { };
    const promise = new Promise<T>(promiseResolve => {
        resolve = promiseResolve;
    });
    return { promise, resolve };
}
