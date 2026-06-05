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
import type { AppHostDiscoveryService, AppHostDiscoverySnapshot, CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import * as cliModule from '../debugger/languages/cli';
import * as configInfoProvider from '../utils/configInfoProvider';
import { describeIncludeDisabledCommandsCapability, lsJsonStreamCapability } from '../types/configInfo';

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

function createDeferredDiscoveryService(): { appHostDiscoveryService: AppHostDiscoveryService; discovery: { promise: Promise<AppHostDiscoverySnapshot>; resolve: (snapshot: AppHostDiscoverySnapshot) => void } } {
    const discovery = createDeferred<AppHostDiscoverySnapshot>();
    const appHostDiscoveryService = {
        onDidChangeCandidates: () => ({ dispose: () => { } }),
        discover: async () => (await discovery.promise).candidates,
        discoverStream: async function* () {
            yield await discovery.promise;
        },
        dispose: () => { },
    } as unknown as AppHostDiscoveryService;

    return { appHostDiscoveryService, discovery };
}

suite('AppHostDataRepository', () => {
    let terminalProvider: AspireTerminalProvider;
    let subscriptions: vscode.Disposable[];
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;
    let getConfigInfoStub: sinon.SinonStub;
    let defaultWorkspaceFoldersStub: sinon.SinonStub;
    let findFilesStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
        // The repository probes `aspire config info --json` to learn whether the CLI advertises the
        // describe `--include-disabled-commands` capability. Default to a capability-supporting CLI
        // so the common-path tests below still see the flag on the describe invocation.
        getConfigInfoStub = sinon.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            capabilities: [describeIncludeDisabledCommandsCapability],
        } as any);
        defaultWorkspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        findFilesStub = sinon.stub(vscode.workspace, 'findFiles').resolves([]);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        getConfigInfoStub.restore();
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
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands']);

        repository.dispose();
    });

    test('describe watch omits disabled command flag when CLI does not advertise the capability', async () => {
        // A CLI that responds to `config info` but doesn't list the capability is authoritative:
        // we must not pass the flag at all (no optimistic attempt, no error-text parsing).
        getConfigInfoStub.resolves({ capabilities: ['pipelines'] } as any);

        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json']);

        repository.dispose();
    });

    test('describe watch optimistically sends disabled command flag when capabilities cannot be read', async () => {
        // If `config info` can't be read (e.g. a CLI too old to support it) we keep the optimistic
        // default so newer-but-unprobeable CLIs still get the flag; the no-data fallback protects us.
        getConfigInfoStub.resolves(null);

        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands']);

        repository.dispose();
    });

    test('describe watch retries without disabled command flag when CLI does not recognize it', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const firstOptions = spawnStub.firstCall.args[3];
        firstOptions.stderrCallback("Unrecognized command or argument '--include-disabled-commands'");
        firstOptions.exitCallback(1);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledTwice, true);
        assert.deepStrictEqual(spawnStub.secondCall.args[2], ['describe', '--follow', '--format', 'json']);

        repository.dispose();
    });

    test('describe watch retries without disabled command flag when CLI does not recognize it in a non-English locale', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        const firstOptions = spawnStub.firstCall.args[3];
        // Localized (Spanish) rejection from a CLI without the flag. The flag token is never
        // translated, so the fallback must trigger regardless of the surrounding error text.
        firstOptions.stderrCallback("No se encuentra el recurso '--include-disabled-commands'.");
        firstOptions.exitCallback(1);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledTwice, true);
        assert.deepStrictEqual(spawnStub.secondCall.args[2], ['describe', '--follow', '--format', 'json']);

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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            return new TestChildProcess();
        });
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

            getAppHostsLineCallback(JSON.stringify([{
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]));
            await waitForAppHostDiscovery();

            await waitForCondition(
                () => spawnStub.getCalls().some(call => (call.args[2] as string[] | undefined)?.[0] === 'describe'),
                'describe watch did not start');
            const describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            assert.ok(describeCall);
            const exitCallback = describeCall.args[3].exitCallback;
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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            return new TestChildProcess();
        });
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

            getAppHostsLineCallback(JSON.stringify([{
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]));
            await waitForAppHostDiscovery();

            await waitForCondition(
                () => spawnStub.getCalls().some(call => (call.args[2] as string[] | undefined)?.[0] === 'describe'),
                'describe watch did not start');
            const describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            assert.ok(describeCall);
            const exitCallback = describeCall.args[3].exitCallback;
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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
                return getAppHostsProcess;
            }
            if (args[0] === 'ps') {
                return psProcess;
            }
            return new TestChildProcess();
        });
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

            getAppHostsLineCallback(JSON.stringify([{
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]));
            await waitForAppHostDiscovery();

            await waitForCondition(
                () => spawnStub.getCalls().some(call => (call.args[2] as string[] | undefined)?.[0] === 'describe'),
                'describe watch did not start');
            const describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            assert.ok(describeCall);
            const describeErrorCallback = describeCall.args[3].errorCallback;
            describeErrorCallback(new Error('describe failed'));
            assert.ok(repository.errorMessage?.includes('describe failed'), repository.errorMessage);

            const psCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'ps');
            assert.ok(psCall);
            const psOptions = psCall.args[3];
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
        const describeProcesses = [describeProcess, replacementDescribeProcess];
        const psProcesses = [psFollowProcess, psFallbackProcess, psSuccessProcess];
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            switch (args[0]) {
                case 'ls':
                    getAppHostsLineCallback = createLsLineCallback(options);
                    return getAppHostsProcess;
                case 'describe':
                    return describeProcesses.shift() ?? new TestChildProcess();
                case 'ps':
                    return psProcesses.shift() ?? new TestChildProcess();
                default:
                    return new TestChildProcess();
            }
        });
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

            const psFollowCall = spawnStub.getCalls().filter(call => call.args[2][0] === 'ps' && typeof call.args[3].lineCallback === 'function').at(-1);
            assert.ok(psFollowCall);
            const psFollowOptions = psFollowCall.args[3];
            psFollowOptions.exitCallback(1);
            await waitForCondition(() => spawnStub.getCalls().filter(call => call.args[2][0] === 'ps').some(call => typeof call.args[3].stderrCallback === 'function'), 'ps fallback did not start');

            const psFallbackCall = spawnStub.getCalls().filter(call => call.args[2][0] === 'ps').find(call => typeof call.args[3].stderrCallback === 'function');
            assert.ok(psFallbackCall);
            const psFallbackOptions = psFallbackCall.args[3];
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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            switch (args[0]) {
                case 'ls':
                    getAppHostsLineCallback = createLsLineCallback(options);
                    return getAppHostsProcess;
                case 'describe':
                    return describeProcess;
                case 'ps':
                    return psProcess;
                default:
                    return new TestChildProcess();
            }
        });
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
            const spawnArgs = spawnStub.getCalls().map(call => call.args[2] as string[]);
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['ps', '--follow', '--format', 'json'])));
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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            switch (args[0]) {
                case 'ls':
                    getAppHostsLineCallback = createLsLineCallback(options);
                    return getAppHostsProcess;
                case 'describe':
                    return describeProcess;
                case 'ps':
                    return psProcess;
                default:
                    return new TestChildProcess();
            }
        });
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
            const spawnArgs = spawnStub.getCalls().map(call => call.args[2] as string[]);
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/apps/Store/AppHost.csproj'])));
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['ps', '--follow', '--format', 'json'])));
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
            assert.deepStrictEqual(describeCalls[0].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/apps/Store/AppHost.csproj']);
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
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/samples/Store/AppHost.csproj']);

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
            assert.deepStrictEqual(describeCalls[0].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/apps/Store/AppHost.csproj']);
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
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/samples/Store/AppHost.csproj']);
            assert.deepStrictEqual(describeCalls[2].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/tools/Admin/AppHost.csproj']);
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
            assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/samples/Store/AppHost.csproj']);

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
            const spawnArgs = spawnStub.getCalls().map(call => call.args[2] as string[]);
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', configuredAppHostPath])));

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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
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

            await waitForCondition(
                () => executeCommandStub.getCalls().some(call =>
                    call.args[0] === 'setContext' && call.args[1] === 'aspire.loading' && call.args[2] === false),
                'workspace loading context did not clear');
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
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
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

            await waitForCondition(
                () => executeCommandStub.getCalls().some(call =>
                    call.args[0] === 'setContext' && call.args[1] === 'aspire.loading' && call.args[2] === false),
                'workspace loading context did not clear');
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

    test('workspace discovery applies streamed AppHost candidates before completion', async () => {
        getConfigInfoStub.resolves({
            capabilities: [describeIncludeDisabledCommandsCapability, lsJsonStreamCapability],
        } as any);
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let lsOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                lsOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => lsOptions !== undefined, 'streaming aspire ls did not start');

            lsOptions.lineCallback(JSON.stringify({
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }));
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.isLoading, false);
            assert.deepStrictEqual(repository.workspaceAppHostCandidatePaths, [
                '/workspace/apps/Store/AppHost.csproj',
            ]);
            assert.strictEqual(repository.workspaceAppHostPath, undefined);

            lsOptions.lineCallback(JSON.stringify({
                path: '/workspace/samples/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }));
            lsOptions.exitCallback(0);
            await waitForCondition(() => repository.isWorkspaceAppHostDiscoveryComplete, 'streaming aspire ls did not complete');

            assert.deepStrictEqual(repository.workspaceAppHostCandidatePaths, [
                '/workspace/apps/Store/AppHost.csproj',
                '/workspace/samples/Store/AppHost.csproj',
            ]);
        } finally {
            repository.dispose();
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
            discoverStream: async function* () {
                yield { candidates, isComplete: true };
            },
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

    test('workspace ps shows running AppHosts before workspace discovery completes', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const { appHostDiscoveryService, discovery } = createDeferredDiscoveryService();
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
            await waitForCondition(() => psOptions !== undefined, 'workspace ps watch did not start before discovery completed');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(repository.isLoading, false);

            discovery.resolve({
                candidates: [{
                    path: '/workspace/apps/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                }],
                isComplete: true,
            });
            await waitForCondition(() => repository.isWorkspaceAppHostDiscoveryComplete, 'workspace discovery did not complete');

            assert.deepStrictEqual(repository.workspaceAppHostCandidatePaths, [
                '/workspace/apps/Store/AppHost.csproj',
            ]);
            assert.strictEqual(repository.appHosts.length, 1);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('refresh restarts workspace ps follow while workspace discovery is pending', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const { appHostDiscoveryService } = createDeferredDiscoveryService();
        const psProcesses: TestChildProcess[] = [];
        spawnStub.callsFake((_terminalProvider, _command, args) => {
            const process = new TestChildProcess();
            if (args[0] === 'ps') {
                psProcesses.push(process);
            }

            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => psProcesses.length === 1, 'workspace ps follow did not start before discovery completed');

            repository.refresh();
            await waitForCondition(() => psProcesses.length === 2, 'workspace ps follow did not restart on refresh');

            assert.strictEqual(psProcesses[0].killed, true);
            assert.deepStrictEqual(
                spawnStub.getCalls()
                    .map(call => call.args[2])
                    .filter(args => args[0] === 'ps'),
                [
                    ['ps', '--follow', '--format', 'json'],
                    ['ps', '--follow', '--format', 'json'],
                ]);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps keeps all running AppHosts while streamed discovery is incomplete', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const finalDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => (await finalDiscovery.promise).candidates,
            discoverStream: async function* () {
                yield {
                    candidates: [{
                        path: '/workspace/apps/Store/AppHost.csproj',
                        language: 'csharp',
                        status: 'buildable',
                    }],
                    isComplete: false,
                };
                yield await finalDiscovery.promise;
            },
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => repository.workspaceAppHostCandidatePaths.length === 1, 'streamed workspace candidate did not appear');
            await waitForCondition(() => psOptions !== undefined, 'workspace ps watch did not start before discovery completed');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                },
                {
                    appHostPath: '/workspace/samples/Store/AppHost.csproj',
                    appHostPid: 125882,
                },
            ]));

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.deepStrictEqual(repository.appHosts.map(appHost => appHost.appHostPath), [
                '/workspace/apps/Store/AppHost.csproj',
                '/workspace/samples/Store/AppHost.csproj',
            ]);
        } finally {
            finalDiscovery.resolve({ candidates: [], isComplete: true });
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('coalesces workspace discovery change events while discovery is in flight', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryChanges = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        const firstDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const secondDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const discoverStreamStub = sinon.stub();
        discoverStreamStub.onFirstCall().callsFake(async function* () {
            yield await firstDiscovery.promise;
        });
        discoverStreamStub.onSecondCall().callsFake(async function* () {
            yield await secondDiscovery.promise;
        });
        const appHostDiscoveryService = {
            onDidChangeCandidates: discoveryChanges.event,
            discover: async () => [],
            discoverStream: discoverStreamStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStreamStub.callCount, 1);

            discoveryChanges.fire(workspaceFolder);
            discoveryChanges.fire(workspaceFolder);
            discoveryChanges.fire(workspaceFolder);
            await waitForMicrotasks();

            assert.strictEqual(discoverStreamStub.callCount, 1);

            firstDiscovery.resolve({ candidates: [], isComplete: true });
            await waitForCondition(() => discoverStreamStub.callCount === 2, 'pending workspace discovery did not run');
            secondDiscovery.resolve({ candidates: [], isComplete: true });
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStreamStub.callCount, 2);
        } finally {
            repository.dispose();
            discoveryChanges.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('queues forced workspace discovery refresh without starting overlapping discovery', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const firstDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const secondDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        let firstTokenCancelled = false;
        const discoverStreamStub = sinon.stub();
        discoverStreamStub.onFirstCall().callsFake(async function* (_folder: vscode.WorkspaceFolder, _forceRefresh?: boolean, cancellationToken?: vscode.CancellationToken) {
            cancellationToken?.onCancellationRequested(() => {
                firstTokenCancelled = true;
            });
            yield await firstDiscovery.promise;
        });
        discoverStreamStub.onSecondCall().callsFake(async function* (_folder: vscode.WorkspaceFolder, forceRefresh?: boolean) {
            assert.strictEqual(forceRefresh, true);
            yield await secondDiscovery.promise;
        });
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => [],
            discoverStream: discoverStreamStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStreamStub.callCount, 1);

            repository.refresh();
            await waitForMicrotasks();

            assert.strictEqual(firstTokenCancelled, false);
            assert.strictEqual(discoverStreamStub.callCount, 1);

            firstDiscovery.resolve({ candidates: [], isComplete: true });
            await waitForCondition(() => discoverStreamStub.callCount === 2, 'queued forced workspace discovery did not run');
            secondDiscovery.resolve({ candidates: [], isComplete: true });
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStreamStub.callCount, 2);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps ignores running AppHosts outside workspace before discovery completes', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const { appHostDiscoveryService } = createDeferredDiscoveryService();
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
            await waitForCondition(() => psOptions !== undefined, 'workspace ps watch did not start before discovery completed');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/other/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.appHosts.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps empty snapshot keeps loading while workspace discovery is pending', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const { appHostDiscoveryService } = createDeferredDiscoveryService();
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
            await waitForCondition(() => psOptions !== undefined, 'workspace ps watch did not start before discovery completed');

            psOptions.lineCallback(JSON.stringify([]));

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.isLoading, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps stops after discovery completes with no buildable AppHosts', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const { appHostDiscoveryService, discovery } = createDeferredDiscoveryService();
        let psProcess: TestChildProcess | undefined;
        spawnStub.callsFake((_terminalProvider, _command, args) => {
            const process = new TestChildProcess();
            if (args[0] === 'ps') {
                psProcess = process;
            }
            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => psProcess !== undefined, 'workspace ps watch did not start before discovery completed');

            discovery.resolve({
                candidates: [{
                    path: '/workspace/apps/Store/Store.csproj',
                    language: 'csharp',
                    status: 'possibly-unbuildable',
                }],
                isComplete: true,
            });
            await waitForCondition(() => repository.isWorkspaceAppHostDiscoveryComplete, 'workspace discovery did not complete');

            assert.strictEqual(psProcess?.killed, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('does not apply stale in-flight workspace discovery after refresh is queued', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const firstDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const secondDiscovery = createDeferred<AppHostDiscoverySnapshot>();
        const discoverStreamStub = sinon.stub();
        discoverStreamStub.onFirstCall().callsFake(async function* () {
            yield await firstDiscovery.promise;
        });
        discoverStreamStub.onSecondCall().callsFake(async function* () {
            yield await secondDiscovery.promise;
        });
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => [],
            discoverStream: discoverStreamStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStreamStub.callCount, 1);

            repository.refresh();
            await waitForMicrotasks();
            assert.strictEqual(discoverStreamStub.callCount, 1);

            firstDiscovery.resolve({
                candidates: [{
                    path: '/workspace/stale/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                }],
                isComplete: true,
            });
            await waitForCondition(() => discoverStreamStub.callCount === 2, 'queued forced workspace discovery did not run');

            assert.strictEqual(repository.workspaceAppHostPath, undefined);

            secondDiscovery.resolve({
                candidates: [{
                    path: '/workspace/current/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                }],
                isComplete: true,
            });
            await waitForCondition(() => repository.workspaceAppHostPath === '/workspace/current/AppHost.csproj', 'current workspace discovery did not apply');
        } finally {
            repository.dispose();
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

            const psFollowCall = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps' && typeof call.args[3].lineCallback === 'function').at(-1);
            assert.ok(psFollowCall);
            const psFollowOptions = psFollowCall.args[3];
            psFollowOptions.exitCallback(1);
            await waitForCondition(() => spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps').some(call => typeof call.args[3].stderrCallback === 'function'), 'ps fallback did not start');

            const psFallbackCall = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps').find(call => typeof call.args[3].stderrCallback === 'function');
            assert.ok(psFallbackCall);
            const psFallbackOptions = psFallbackCall.args[3];
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
            discoverStream: async function* () {
                yield {
                    candidates: [
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
                    isComplete: true,
                };
            },
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
            discoverStream: async function* () {
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

    test('workspace describe reports compatibility error when running AppHost returns no data successfully', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let psOptions: any;
        const describeOptions: any[] = [];
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions.push(options);
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
            assert.strictEqual(repository.hasError, false);

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

            describeOptions[1].exitCallback(0);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire.Hosting 13.2.0'), repository.errorMessage);
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
    let getConfigInfoStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
        // Stub the capability probe so the constructor's eager `config info --json` doesn't
        // spawn through spawnCliProcess and pollute these suites' spawn assertions.
        getConfigInfoStub = sinon.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            capabilities: [describeIncludeDisabledCommandsCapability],
        } as any);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        getConfigInfoStub.restore();
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
        assert.deepStrictEqual(describeCall.args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

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

    test('global describe retries without disabled command flag when CLI does not recognize it', async () => {
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
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

        const firstDescribe = spawned.find(call => call.args[0] === 'describe');
        assert.ok(firstDescribe);
        assert.deepStrictEqual(firstDescribe.args, ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

        firstDescribe.options.stderrCallback("Unrecognized command or argument '--include-disabled-commands'");
        firstDescribe.options.exitCallback(1);
        await waitForMicrotasks();

        const describeCalls = spawned.filter(call => call.args[0] === 'describe');
        assert.strictEqual(describeCalls.length, 2);
        assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('global view keeps running AppHosts when workspace discovery finds no buildable candidates', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const discoveryChanges = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        let candidates: CandidateAppHostDisplayInfo[] = [{
            path: '/workspace/apps/Store/AppHost.csproj',
            language: 'csharp',
            status: 'buildable',
            selected: true,
        }];
        const discoveryService = {
            discover: async () => candidates,
            discoverStream: async function* () {
                yield { candidates, isComplete: true };
            },
            onDidChangeCandidates: discoveryChanges.event,
            dispose: () => discoveryChanges.dispose(),
        } as unknown as AppHostDiscoveryService;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForCondition(() => psOptions !== undefined, 'global ps watch did not start');

            psOptions.lineCallback(JSON.stringify({
                appHostPath: '/running/AppHost.csproj',
                appHostPid: 125881,
                cliPid: 125738,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }));
            await waitForCondition(() => repository.appHosts.length === 1, 'global AppHost ps delta was not applied');

            candidates = [{
                path: '/workspace/apps/Store/Store.csproj',
                language: 'csharp',
                status: 'possibly-unbuildable',
            }];
            discoveryChanges.fire(workspaceFolder);
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.viewMode, 'global');
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, '/running/AppHost.csproj');
            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            discoveryChanges.dispose();
        }
    });

    test('global view keeps running AppHosts when workspace discovery finds multiple buildable candidates', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const discoveryChanges = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        let candidates: CandidateAppHostDisplayInfo[] = [{
            path: '/workspace/apps/Store/AppHost.csproj',
            language: 'csharp',
            status: 'buildable',
            selected: true,
        }];
        const discoveryService = {
            discover: async () => candidates,
            discoverStream: async function* () {
                yield { candidates, isComplete: true };
            },
            onDidChangeCandidates: discoveryChanges.event,
            dispose: () => discoveryChanges.dispose(),
        } as unknown as AppHostDiscoveryService;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForCondition(() => psOptions !== undefined, 'global ps watch did not start');

            psOptions.lineCallback(JSON.stringify({
                appHostPath: '/running/AppHost.csproj',
                appHostPid: 125881,
                cliPid: 125738,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }));
            await waitForCondition(() => repository.appHosts.length === 1, 'global AppHost ps delta was not applied');

            candidates = [{
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }, {
                path: '/workspace/samples/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }];
            discoveryChanges.fire(workspaceFolder);
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.viewMode, 'global');
            assert.deepStrictEqual(repository.workspaceAppHostCandidatePaths, [
                '/workspace/apps/Store/AppHost.csproj',
                '/workspace/samples/Store/AppHost.csproj',
            ]);
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, '/running/AppHost.csproj');
            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            discoveryChanges.dispose();
        }
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
    let getConfigInfoStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake(() => new TestChildProcess());
        // Stub the capability probe so the constructor's eager `config info --json` doesn't
        // spawn through spawnCliProcess and pollute these suites' spawn assertions.
        getConfigInfoStub = sinon.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            capabilities: [describeIncludeDisabledCommandsCapability],
        } as any);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        getConfigInfoStub.restore();
        subscriptions.forEach(subscription => subscription.dispose());
    });

    test('opening AppHost file with hidden panel starts describe watch', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFileOpen(true);
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands']);

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
    // Flush many microtask ticks so promise chains settle, including the multi-await
    // capability probe (cli path -> ensureCapabilities -> config info) in the describe
    // start path. We deliberately avoid a real timer (setTimeout): several tests install
    // sinon fake timers, under which a setTimeout(0) would never fire and would hang here.
    for (let i = 0; i < 20; i++) {
        await Promise.resolve();
    }
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
