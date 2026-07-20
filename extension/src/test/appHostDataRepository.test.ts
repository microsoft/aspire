import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { AppHostDataRepository, AspireCliFailedError } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import type { AppHostDiscoveryService, CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import * as cliModule from '../debugger/languages/cli';
import * as configInfoProvider from '../utils/configInfoProvider';
import { describeIncludeDisabledCommandsCapability } from '../types/configInfo';

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

    async function startDescribeForRunningAppHost(repository: AppHostDataRepository, appHostPath: string): Promise<sinon.SinonSpyCall> {
        const psCall = spawnStub.getCalls().find(call => {
            const args = call.args[2] as string[];
            return args[0] === 'ps' && args.includes('--follow');
        });
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath,
            appHostPid: 1,
        }]));
        await waitForMicrotasks();

        const describeCall = spawnStub.getCalls().find(call => {
            const args = call.args[2] as string[];
            return args[0] === 'describe' && args[args.length - 1] === appHostPath;
        });
        assert.ok(describeCall, `expected a describe stream for ${appHostPath}`);
        return describeCall;
    }

    test('activate does not start describe while panel is hidden', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        await waitForMicrotasks();

        assert.strictEqual(getCliPathStub.called, false);
        assert.strictEqual(spawnStub.called, false);

        repository.dispose();
    });

    test('restarts ps polling immediately after a workspace-folder change while discovery is stuck', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        // Capture the folder-change listener the repository registers so the test can fire it directly.
        let folderChangeListener: ((event: vscode.WorkspaceFoldersChangeEvent) => void) | undefined;
        const onDidChangeWorkspaceFoldersStub = sinon.stub(vscode.workspace, 'onDidChangeWorkspaceFolders')
            .callsFake((listener: any) => {
                folderChangeListener = listener;
                return { dispose: () => { } };
            });
        // A discovery that never resolves keeps `_workspaceAppHostDiscoveryComplete` false, so the only
        // path that can restart ps is the folder-change handler itself (not a discovery-completion branch).
        const appHostDiscoveryService = {
            onDidChangeCandidates: new vscode.EventEmitter<vscode.WorkspaceFolder>().event,
            discover: () => new Promise<CandidateAppHostDisplayInfo[]>(() => { }),
            dispose: () => { },
        };
        const countPsSpawns = () => spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps').length;

        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => countPsSpawns() >= 1, 'expected initial ps watch to start');
            const psSpawnsBeforeFolderChange = countPsSpawns();

            assert.ok(folderChangeListener, 'expected the repository to register a workspace-folder listener');
            folderChangeListener({ added: [], removed: [] });

            // ps must restart right away — before the (stuck) discovery resolves — rather than leaving an
            // active panel with no running-state source.
            await waitForCondition(() => countPsSpawns() > psSpawnsBeforeFolderChange,
                'expected ps polling to restart immediately after the workspace-folder change');
            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
        } finally {
            repository.dispose();
            onDidChangeWorkspaceFoldersStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('visible workspace panel starts a describe for a running AppHost', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));

        let describeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            return describeCall !== undefined;
        }, 'expected a describe stream to start');

        assert.strictEqual(getCliPathStub.called, true);
    assert.deepStrictEqual(describeCall!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('describe omits disabled command flag when CLI does not advertise the capability', async () => {
        // A CLI that responds to `config info` but doesn't list the capability is authoritative:
        // we must not pass the flag at all (no optimistic attempt, no error-text parsing).
        getConfigInfoStub.resolves({ capabilities: ['pipelines'] } as any);

        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));

        let describeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            return describeCall !== undefined;
        }, 'expected a describe stream to start');

        assert.deepStrictEqual(describeCall!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('describe retries without nologo when an older CLI rejects it', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));

            let describeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                describeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
                });
                return describeCall !== undefined;
            }, 'expected a describe stream to start');

            assert.deepStrictEqual(describeCall!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);
            describeCall!.args[3].stderrCallback("Unrecognized command or argument '--nologo'.");
            describeCall!.args[3].exitCallback(1);
            await waitForMicrotasks();
            await waitForMicrotasks();

            const describeCalls = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe');
            assert.strictEqual(describeCalls.length, 2);
            assert.deepStrictEqual(describeCalls[1].args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);
        } finally {
            repository.dispose();
        }
    });

    test('concurrent older-CLI nologo failures each retry their captured command', async () => {
        const firstDescribeProcess = new TestChildProcess();
        const firstPsProcess = new TestChildProcess();
        const retryDescribeProcess = new TestChildProcess();
        const retryPsProcess = new TestChildProcess();
        let describeSpawnCount = 0;
        let oneShotPsSpawnCount = 0;
        spawnStub.callsFake((_terminalProvider, _command, args) => {
            if (args[0] === 'describe' && args.includes('--follow')) {
                return describeSpawnCount++ === 0 ? firstDescribeProcess : retryDescribeProcess;
            }

            if (args[0] === 'ps' && !args.includes('--follow')) {
                return oneShotPsSpawnCount++ === 0 ? firstPsProcess : retryPsProcess;
            }

            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psFollowCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && spawnArgs.includes('--follow');
            });
            assert.ok(psFollowCall, 'expected an aspire ps --follow watch to be running');
            psFollowCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));

            let firstDescribe: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                firstDescribe = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
                });
                return firstDescribe !== undefined;
            }, 'expected a describe stream to start');

            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            const firstOneShotPs = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && !spawnArgs.includes('--follow');
            });
            assert.ok(firstOneShotPs, 'expected fetchAppHostsOnce to spawn one-shot ps');
            assert.deepStrictEqual(firstDescribe!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);
            assert.deepStrictEqual(firstOneShotPs.args[2], ['ps', '--format', 'json', '--nologo']);
            assert.strictEqual(firstDescribe!.returnValue, firstDescribeProcess);
            assert.strictEqual(firstOneShotPs.returnValue, firstPsProcess);

            firstDescribe!.args[3].stderrCallback("Unrecognized command or argument '--nologo'.");
            firstDescribe!.args[3].exitCallback(1);
            firstOneShotPs.args[3].stderrCallback("Unrecognized command or argument '--nologo'.");
            firstOneShotPs.args[3].exitCallback(1);
            await waitForMicrotasks();
            await waitForMicrotasks();

            const describeCalls = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe');
            assert.strictEqual(describeCalls.length, 2);
            assert.deepStrictEqual(describeCalls[1].args[2], ['describe', '--follow', '--format', 'json', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);
            assert.strictEqual(describeCalls[1].returnValue, retryDescribeProcess);

            const oneShotPsCalls = spawnStub.getCalls().filter(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && !spawnArgs.includes('--follow');
            });
            assert.strictEqual(oneShotPsCalls.length, 2);
            assert.deepStrictEqual(oneShotPsCalls[1].args[2], ['ps', '--format', 'json']);
            assert.strictEqual(oneShotPsCalls[1].returnValue, retryPsProcess);
            oneShotPsCalls[1].args[3].stdoutCallback('[]');
            oneShotPsCalls[1].args[3].exitCallback(0);

            const appHosts = await fetchPromise;
            assert.deepStrictEqual(appHosts, []);
        } finally {
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce uses ps without resources and describes each AppHost', async () => {
        const psProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(psProcess);
        spawnStub.onSecondCall().returns(describeProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--format', 'json', '--nologo']);
            assert.strictEqual(spawnStub.firstCall.args[3].noExtensionVariables, true);

            spawnStub.firstCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                dashboardUrl: 'https://localhost:1234',
                cliPid: 5678,
                resources: null,
            }]));
            spawnStub.firstCall.args[3].exitCallback(0);
            await waitForMicrotasks();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['describe', '--format', 'json', '--nologo', '--apphost', '/workspace/AppHost.csproj']);
            assert.strictEqual(spawnStub.secondCall.args[3].noExtensionVariables, true);

            spawnStub.secondCall.args[3].stdoutCallback(JSON.stringify({
                resources: [{
                    name: 'api',
                    displayName: 'api',
                    resourceType: 'Project',
                    state: 'Running',
                    stateStyle: null,
                    healthStatus: null,
                    healthReports: null,
                    exitCode: null,
                    dashboardUrl: null,
                    urls: [],
                    commands: null,
                    properties: null,
                }]
            }));
            spawnStub.secondCall.args[3].exitCallback(0);

            const appHosts = await fetchPromise;

            assert.strictEqual(describeProcess.killed, true);
            assert.strictEqual(appHosts.length, 1);
            assert.strictEqual(appHosts[0].resources?.[0].name, 'api');
        } finally {
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce retries without nologo when an older CLI rejects it', async () => {
        const rejectedPsProcess = new TestChildProcess();
        const psProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(rejectedPsProcess);
        spawnStub.onSecondCall().returns(psProcess);
        spawnStub.onThirdCall().returns(describeProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--format', 'json', '--nologo']);
            spawnStub.firstCall.args[3].stderrCallback("Unrecognized command or argument '--nologo'.");
            spawnStub.firstCall.args[3].exitCallback(1);
            await waitForMicrotasks();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['ps', '--format', 'json']);
            spawnStub.secondCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                dashboardUrl: 'https://localhost:1234',
                cliPid: 5678,
                resources: null,
            }]));
            spawnStub.secondCall.args[3].exitCallback(0);
            await waitForMicrotasks();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.thirdCall.args[2], ['describe', '--format', 'json', '--apphost', '/workspace/AppHost.csproj']);
            spawnStub.thirdCall.args[3].stdoutCallback(JSON.stringify({ resources: [] }));
            spawnStub.thirdCall.args[3].exitCallback(0);

            const appHosts = await fetchPromise;

            assert.strictEqual(appHosts.length, 1);
            assert.deepStrictEqual(appHosts[0].resources, []);
        } finally {
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce rejects ps failures with CLI diagnostics', async () => {
        const psProcess = new TestChildProcess();
        spawnStub.returns(psProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stderrCallback('Unrecognized command or argument --resources');
            spawnStub.firstCall.args[3].exitCallback(2);

            await assert.rejects(fetchPromise, (error: unknown) => {
                assert.ok(error instanceof AspireCliFailedError);
                assert.doesNotMatch(error.message, /Unrecognized command or argument --resources/);
                assert.match(error.stderr, /Unrecognized command or argument --resources/);
                return true;
            });
        } finally {
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce times out hung ps process', async () => {
        const clock = sinon.useFakeTimers();
        const psProcess = new TestChildProcess();
        spawnStub.returns(psProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            await clock.tickAsync(30000);

            await assert.rejects(fetchPromise, (error: unknown) => {
                assert.ok(error instanceof AspireCliFailedError);
                assert.doesNotMatch(error.message, /timed out after 30000ms/);
                assert.match(error.stderr, /timed out after 30000ms/);
                return true;
            });
            assert.strictEqual(psProcess.killed, true);
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('runResourceCommand uses one-shot CLI runner', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('api', ' /workspace/AppHost.csproj ', 'stop');
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['resource', 'api', 'stop', '--non-interactive', '--apphost', '/workspace/AppHost.csproj']);
            assert.strictEqual(spawnStub.firstCall.args[3].noExtensionVariables, true);
            assert.deepStrictEqual(spawnStub.firstCall.args[3].env, [{ name: 'ASPIRE_NON_INTERACTIVE', value: 'true' }]);

            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            await runPromise;
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand rejects failures with CLI diagnostics', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('api', '/workspace/AppHost.csproj', 'start');
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stderrCallback('resource is disabled');
            resourceProcess.markExited(1);
            spawnStub.firstCall.args[3].exitCallback(1);

            await assert.rejects(runPromise, (error: unknown) => {
                assert.ok(error instanceof AspireCliFailedError);
                assert.doesNotMatch(error.message, /resource is disabled/);
                assert.match(error.stderr, /resource is disabled/);
                return true;
            });
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand filters older CLI progress from rejected diagnostics', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'restart');
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stderrCallback("Restarting resource 'cache'...\nport is already in use");
            resourceProcess.markExited(1);
            spawnStub.firstCall.args[3].exitCallback(1);

            await assert.rejects(runPromise, (error: unknown) => {
                assert.ok(error instanceof AspireCliFailedError);
                assert.ok(!error.stderr.includes('Restarting resource'), error.stderr);
                assert.doesNotMatch(error.message, /port is already in use/);
                assert.match(error.stderr, /port is already in use/);
                return true;
            });
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand omits --apphost, forwards additional args, and returns captured output', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            // undefined appHostPath means "let the CLI resolve the running AppHost", so no --apphost
            // flag is added. additionalArgs already carry the `--` delimiter from
            // buildResourceCommandCliArgs and must be forwarded verbatim after the command name.
            const runPromise = repository.runResourceCommand('cache', undefined, 'echo-arguments', ['--', '--message=hello']);
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['resource', 'cache', 'echo-arguments', '--non-interactive', '--', '--message=hello']);

            spawnStub.firstCall.args[3].stdoutCallback('rendered command value');
            spawnStub.firstCall.args[3].stderrCallback('status: ok');
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.strictEqual(output.stdout, 'rendered command value');
            assert.strictEqual(output.stderr, 'status: ok');
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand ignores older CLI progress and success stdout for lifecycle commands', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'restart');
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stdoutCallback("Restarting resource 'cache'...\nResource 'cache' restarted successfully.\n");
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.strictEqual(output.stdout, '');
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand preserves real command output after older CLI status stdout', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'echo-arguments');
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stdoutCallback([
                "Validating and executing command 'echo-arguments' on resource 'cache'...",
                "Command 'echo-arguments' executed successfully on resource 'cache'.",
                '{"message":"hello"}'
            ].join('\n'));
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.strictEqual(output.stdout, '{"message":"hello"}');
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand ignores older CLI custom command progress stdout without command output', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'echo-arguments');
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stdoutCallback("Executing command 'echo-arguments' on resource 'cache'...\n");
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.strictEqual(output.stdout, '');
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand marks bounded JSON stdout as truncated', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'dump');
            await waitForMicrotasks();

            const largeOutput = `{"prefix":"${'x'.repeat(70000)}","tail":true}`;
            spawnStub.firstCall.args[3].stdoutCallback(largeOutput);
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.ok(output.stdout.length < largeOutput.length, 'stdout should be capped');
            assert.ok(output.stdout.startsWith('{"prefix":"'), output.stdout.slice(0, 100));
            assert.ok(output.stdout.includes('Aspire command output truncated'), output.stdout);
            assert.ok(output.stdout.endsWith('","tail":true}'), output.stdout.slice(-100));
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand marks bounded chunked text stdout as truncated', async () => {
        const resourceProcess = new TestChildProcess();
        spawnStub.returns(resourceProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'dump');
            await waitForMicrotasks();

            const firstChunk = `text-start\n${'x'.repeat(40000)}`;
            const secondChunk = `${'y'.repeat(40000)}\ntext-end`;
            spawnStub.firstCall.args[3].stdoutCallback(firstChunk);
            spawnStub.firstCall.args[3].stdoutCallback(secondChunk);
            resourceProcess.markExited(0);
            spawnStub.firstCall.args[3].exitCallback(0);

            const output = await runPromise;
            assert.ok(output.stdout.length < firstChunk.length + secondChunk.length, 'stdout should be capped');
            assert.ok(output.stdout.startsWith('text-start\n'), output.stdout.slice(0, 100));
            assert.ok(output.stdout.includes('Aspire command output truncated'), output.stdout);
            assert.ok(output.stdout.endsWith('\ntext-end'), output.stdout.slice(-100));
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand cancels the hidden CLI process through the cancellation token', async () => {
        const resourceProcess = new TestChildProcess(false);
        spawnStub.returns(resourceProcess);
        const cancellationSource = new vscode.CancellationTokenSource();
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const runPromise = repository.runResourceCommand('cache', '/workspace/AppHost.csproj', 'restart', [], cancellationSource.token);
            await waitForMicrotasks();

            cancellationSource.cancel();
            await assert.rejects(runPromise, (error: unknown) => error instanceof vscode.CancellationError);
            assert.strictEqual(resourceProcess.killed, true);
        } finally {
            cancellationSource.dispose();
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce returns healthy AppHosts when one describe fails', async () => {
        const psProcess = new TestChildProcess();
        const healthyDescribeProcess = new TestChildProcess();
        const failedDescribeProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(psProcess);
        spawnStub.onSecondCall().returns(healthyDescribeProcess);
        spawnStub.onThirdCall().returns(failedDescribeProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                dashboardUrl: 'https://localhost:1234',
                cliPid: 5678,
                resources: null,
            }, {
                appHostPath: '/workspace/DeadAppHost.csproj',
                appHostPid: 4321,
                dashboardUrl: 'https://localhost:4321',
                cliPid: 8765,
                resources: null,
            }]));
            spawnStub.firstCall.args[3].exitCallback(0);
            await waitForMicrotasks();
            await waitForMicrotasks();

            spawnStub.secondCall.args[3].stdoutCallback(JSON.stringify({
                resources: [{
                    name: 'api',
                    displayName: 'api',
                    resourceType: 'Project',
                    state: 'Running',
                    stateStyle: null,
                    healthStatus: null,
                    healthReports: null,
                    exitCode: null,
                    dashboardUrl: null,
                    urls: [],
                    commands: null,
                    properties: null,
                }]
            }));
            spawnStub.secondCall.args[3].exitCallback(0);
            spawnStub.thirdCall.args[3].stderrCallback('describe failed');
            spawnStub.thirdCall.args[3].exitCallback(1);

            const appHosts = await fetchPromise;

            assert.strictEqual(appHosts.length, 2);
            assert.strictEqual(appHosts[0].resources?.[0].name, 'api');
            assert.deepStrictEqual(appHosts[1].resources, []);
            assert.strictEqual(healthyDescribeProcess.killed, true);
            assert.strictEqual(failedDescribeProcess.killed, true);
        } finally {
            repository.dispose();
        }
    });

    test('fetchAppHostsOnce ignores non-JSON describe output before resource data', async () => {
        const psProcess = new TestChildProcess();
        const describeProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(psProcess);
        spawnStub.onSecondCall().returns(describeProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            const fetchPromise = repository.fetchAppHostsOnce();
            await waitForMicrotasks();

            spawnStub.firstCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                dashboardUrl: 'https://localhost:1234',
                cliPid: 5678,
                resources: null,
            }]));
            spawnStub.firstCall.args[3].exitCallback(0);
            await waitForMicrotasks();
            await waitForMicrotasks();

            spawnStub.secondCall.args[3].stdoutCallback(`Starting AppHost...\n${JSON.stringify({
                resources: [{
                    name: 'api',
                    displayName: 'api',
                    resourceType: 'Project',
                    state: 'Running',
                    stateStyle: null,
                    healthStatus: null,
                    healthReports: null,
                    exitCode: null,
                    dashboardUrl: null,
                    urls: [],
                    commands: null,
                    properties: null,
                }]
            })}`);
            spawnStub.secondCall.args[3].exitCallback(0);

            const appHosts = await fetchPromise;

            assert.strictEqual(appHosts.length, 1);
            assert.strictEqual(appHosts[0].resources?.[0].name, 'api');
            assert.strictEqual(describeProcess.killed, true);
        } finally {
            repository.dispose();
        }
    });

    test('runResourceCommand rejects invalid AppHost paths before spawning CLI', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            await assert.rejects(
                () => repository.runResourceCommand('api', '   ', 'start'),
                /appHostPath must be a non-empty absolute path/);
            assert.strictEqual(spawnStub.called, false);
        } finally {
            repository.dispose();
        }
    });

    test('describe optimistically sends disabled command flag when capabilities cannot be read', async () => {
        // If `config info` can't be read (e.g. a CLI too old to support it) we keep the optimistic
        // default so newer-but-unprobeable CLIs still get the flag; the no-data fallback protects us.
        getConfigInfoStub.resolves(null);

        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));

        let describeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
            return describeCall !== undefined;
        }, 'expected a describe stream to start');
        assert.deepStrictEqual(describeCall!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('describe retries without disabled command flag when CLI does not recognize it', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));
        // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
        // primary host's stream by its trailing --apphost argument rather than trusting call order.
        let foundDescribeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            foundDescribeCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
            });
            return foundDescribeCall !== undefined;
        }, `expected a describe stream for /workspace/AppHost.csproj`);
        const describeCall = foundDescribeCall!;
        describeCall.args[3].stderrCallback("Unrecognized command or argument '--include-disabled-commands'");
        describeCall.args[3].exitCallback(1);
        await waitForMicrotasks();

        const describeCalls = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe');
        assert.strictEqual(describeCalls.length, 2);
    assert.deepStrictEqual(describeCalls[1].args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('describe retries without disabled command flag when CLI does not recognize it in a non-English locale', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));
        // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
        // primary host's stream by its trailing --apphost argument rather than trusting call order.
        let foundDescribeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            foundDescribeCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
            });
            return foundDescribeCall !== undefined;
        }, `expected a describe stream for /workspace/AppHost.csproj`);
        const describeCall = foundDescribeCall!;
        // Localized (Spanish) rejection from a CLI without the flag. The flag token is never
        // translated, so the fallback must trigger regardless of the surrounding error text.
        describeCall.args[3].stderrCallback("No se encuentra el recurso '--include-disabled-commands'.");
        describeCall.args[3].exitCallback(1);
        await waitForMicrotasks();

        const describeCalls = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe');
        assert.strictEqual(describeCalls.length, 2);
    assert.deepStrictEqual(describeCalls[1].args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--apphost', '/workspace/AppHost.csproj']);

        repository.dispose();
    });

    test('describe reports minimum CLI version when command help is returned', async () => {
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            const lineCallback = describeCall.args[3].lineCallback;
            const exitCallback = describeCall.args[3].exitCallback;
            lineCallback('Description:');
            lineCallback('Usage:');
            lineCallback('aspire [command] [options]');
            lineCallback('Commands:');
            exitCallback(1);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire CLI 13.2.0'), repository.errorMessage);

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
        }
    });

    test('describe reports minimum CLI version when localized command help is returned', async () => {
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            const lineCallback = describeCall.args[3].lineCallback;
            const exitCallback = describeCall.args[3].exitCallback;
            lineCallback('Descripción:');
            lineCallback('Uso:');
            lineCallback('aspire <comando> [opciones]');
            lineCallback('Comandos:');
            exitCallback(1);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire CLI 13.2.0'), repository.errorMessage);

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], true);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
        }
    });

    test('describe does not report compatibility error when workspace AppHost returns no data successfully', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/other/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/other/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/other/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /other/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            describeCall.args[3].exitCallback(0);

            assert.strictEqual(repository.hasError, false);
            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
        }
    });

    test('describe restart backs off exponentially across repeated no-data exits', async () => {
        // Fake timers let us assert the exact restart intervals. waitForCondition relies on a real
        // setTimeout(0) and would hang here, so this test drives readiness with waitForMicrotasks (a
        // pure microtask flush) and advances restart timers with clock.tickAsync.
        const clock = sinon.useFakeTimers();
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            await waitForMicrotasks();

            const describeCalls = () => spawnStub.getCalls().filter(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
            });
            // Exit the most recent describe stream with a no-data failure, which schedules a restart.
            const exitCurrentDescribe = () => describeCalls().at(-1)!.args[3].exitCallback(1);

            assert.strictEqual(describeCalls().length, 1, 'expected the initial describe stream once ps reports the host running');

            // First no-data exit -> restart scheduled 5s out.
            exitCurrentDescribe();
            await clock.tickAsync(4999);
            assert.strictEqual(describeCalls().length, 1, 'restart fired before its 5s delay');
            await clock.tickAsync(1);
            assert.strictEqual(describeCalls().length, 2, 'first restart did not fire at 5s');

            // Second no-data exit must back off to 10s. On the buggy constant-5s path a third stream
            // would spawn again after only 5s; the backoff keeps it at one stream until 10s elapses.
            exitCurrentDescribe();
            await clock.tickAsync(5000);
            assert.strictEqual(describeCalls().length, 2, 'restart interval did not back off past 5s');
            await clock.tickAsync(5000);
            assert.strictEqual(describeCalls().length, 3, 'second restart did not fire at the backed-off 10s');
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('describe reports generic error when workspace AppHost exits with runtime failure', async () => {
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
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
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

            repository.setAppHostFilesOpen(['/workspace/apps/Store/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/apps/Store/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/apps/Store/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            const lineCallback = describeCall.args[3].lineCallback;
            const stderrCallback = describeCall.args[3].stderrCallback;
            const exitCallback = describeCall.args[3].exitCallback;
            lineCallback('Usage:');
            lineCallback('aspire describe [options]');
            stderrCallback('No container runtime detected');
            exitCallback(1);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('No container runtime detected'), repository.errorMessage);

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('describe clears compatibility error after receiving resource data', async () => {
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
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();
            assert.ok(getAppHostsLineCallback);

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            describeCall.args[3].lineCallback(JSON.stringify({ name: 'api' }));

            assert.strictEqual(repository.hasError, false);
            assert.strictEqual(repository.workspaceResources.length, 1);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps success does not clear describe error', async () => {
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const getAppHostsProcess = new TestChildProcess();
        spawnStub.onFirstCall().callsFake((_terminalProvider, _command, _args, options) => {
            getAppHostsLineCallback = createLsLineCallback(options);
            return getAppHostsProcess;
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

            repository.setAppHostFilesOpen(['/workspace/apps/Store/AppHost.csproj']);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/apps/Store/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/apps/Store/AppHost.csproj`);
            const describeCall = foundDescribeCall!;
            describeCall.args[3].errorCallback(new Error('describe failed'));
            assert.ok(repository.errorMessage?.includes('describe failed'), repository.errorMessage);

            // A subsequent successful ps snapshot clears any *ps* error but must not wipe the describe
            // banner, which reflects a genuine describe-stream failure on a still-running AppHost.
            const laterPsCall = spawnStub.getCalls().find(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            assert.ok(laterPsCall);
            laterPsCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1234,
                cliPid: null,
                dashboardUrl: null,
            }]));
            await waitForMicrotasks();

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
            await waitForCondition(() => spawnStub.getCalls().filter(call => call.args[2][0] === 'ps').some(call => !(call.args[2] as string[]).includes('--follow')), 'ps fallback did not start');

            const psFallbackCall = spawnStub.getCalls().filter(call => call.args[2][0] === 'ps').find(call => !(call.args[2] as string[]).includes('--follow'));
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

    test('host-scoped describe error follows its owner across selection changes and clears when it stops', async () => {
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

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/selected/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/selected/AppHost.csproj',
                    '/workspace/peer-one/AppHost.csproj',
                    '/workspace/peer-two/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            repository.setAppHostFilesOpen([
                '/workspace/selected/AppHost.csproj',
                '/workspace/peer-one/AppHost.csproj',
                '/workspace/peer-two/AppHost.csproj',
            ]);
            await waitForMicrotasks();

            const psCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && spawnArgs.includes('--follow');
            });
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/selected/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=selected',
            }, {
                appHostPath: '/workspace/peer-one/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'https://localhost:17194/login?t=peer-one',
            }, {
                appHostPath: '/workspace/peer-two/AppHost.csproj',
                appHostPid: 3,
                dashboardUrl: 'https://localhost:17195/login?t=peer-two',
            }]));

            await waitForCondition(() => spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe').length >= 3,
                'expected a describe stream for each running AppHost');
            const selectedDescribe = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs.at(-1) === '/workspace/selected/AppHost.csproj';
            });
            assert.ok(selectedDescribe);
            selectedDescribe.args[3].errorCallback(new Error('selected describe failed'));
            assert.ok(repository.errorMessage?.includes('selected describe failed'), repository.errorMessage);

            (repository as unknown as { _setWorkspaceAppHostPathFromCurrentCandidates(appHostPath: string): void })
                ._setWorkspaceAppHostPathFromCurrentCandidates('/workspace/peer-one/AppHost.csproj');
            const peerDescribe = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs.at(-1) === '/workspace/peer-one/AppHost.csproj';
            });
            assert.ok(peerDescribe);
            peerDescribe.args[3].lineCallback(JSON.stringify({ name: 'peer-resource' }));
            await waitForMicrotasks();
            assert.ok(repository.errorMessage?.includes('selected describe failed'), repository.errorMessage);

            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/peer-one/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'https://localhost:17194/login?t=peer-one',
            }, {
                appHostPath: '/workspace/peer-two/AppHost.csproj',
                appHostPid: 3,
                dashboardUrl: 'https://localhost:17195/login?t=peer-two',
            }]));
            await waitForMicrotasks();

            assert.strictEqual(repository.errorMessage, undefined);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('ps cli-path failures surface a single fetch prefix in error message', async () => {
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        getCliPathStub.onFirstCall().resolves('aspire');
        getCliPathStub.onSecondCall().rejects(new Error('cli missing'));

        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.setViewMode('global');
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const followCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(followCall);

            followCall.options.exitCallback(1);
            await waitForCondition(() => repository.hasError, 'expected ps error after cli path failure');

            assert.ok(
                repository.errorMessage?.includes('Error fetching running AppHosts: Error: cli missing'),
                repository.errorMessage
            );
            assert.ok(
                !repository.errorMessage?.includes('Error fetching running AppHosts: Error fetching running AppHosts'),
                repository.errorMessage
            );
        } finally {
            repository.dispose();
        }
    });

    test('stop refresh clears stale apphost', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(initialFollowCall, 'clears stale workspace apphost');
            initialFollowCall.options.lineCallback(JSON.stringify({
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }));
            await waitForCondition(() => repository.workspaceAppHost?.appHostPath === '/workspace/AppHost.csproj', 'workspace apphost did not become running');

            repository.requestAppHostStopRefresh('/workspace/AppHost.csproj');
            clock.tick(400);
            await waitForMicrotasks();

            const snapshotCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--format', 'json', '--nologo']));
            assert.ok(snapshotCall, 'clears stale workspace apphost');
            snapshotCall.options.stdoutCallback(JSON.stringify([]));
            snapshotCall.options.exitCallback(0);
            clock.tick(1);
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHost, undefined, 'clears stale workspace apphost');
            assert.strictEqual(repository.appHosts.length, 0, 'clears stale workspace apphost');
            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo'])).length,
                1,
                'clears stale workspace apphost'
            );
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh preserves running apphost', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(initialFollowCall, 'preserves running apphost');
            initialFollowCall.options.lineCallback(JSON.stringify({
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }));
            await waitForCondition(() => repository.workspaceAppHost?.appHostPath === '/workspace/AppHost.csproj', 'workspace apphost did not become running');

            repository.requestAppHostStopRefresh('/workspace/AppHost.csproj');
            clock.tick(400);
            await waitForMicrotasks();

            const snapshotCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--format', 'json', '--nologo']));
            assert.ok(snapshotCall, 'preserves running apphost');
            snapshotCall.options.stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }]));
            snapshotCall.options.exitCallback(0);
            clock.tick(1);
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/AppHost.csproj', 'preserves running apphost');
            assert.strictEqual(repository.appHosts.length, 1, 'preserves running apphost');
            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo'])).length,
                1,
                'preserves running apphost'
            );
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh retries while apphost remains running', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(initialFollowCall);
            initialFollowCall.options.lineCallback(JSON.stringify({
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }));
            await waitForCondition(() => repository.workspaceAppHost?.appHostPath === '/workspace/AppHost.csproj', 'workspace apphost did not become running');

            repository.requestAppHostStopRefresh('/workspace/AppHost.csproj');
            await clock.tickAsync(400);
            await waitForMicrotasks();

            const snapshotArgs = JSON.stringify(['ps', '--format', 'json', '--nologo']);
            const firstSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(firstSnapshot);
            firstSnapshot.options.stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }]));
            firstSnapshot.options.exitCallback(0);
            await waitForMicrotasks();

            await clock.tickAsync(400);
            await waitForMicrotasks();

            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length,
                2,
                'expected retry snapshot while apphost is still running'
            );

            const secondSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(secondSnapshot);
            secondSnapshot.options.stdoutCallback(JSON.stringify([]));
            secondSnapshot.options.exitCallback(0);
            await waitForMicrotasks();

            await clock.tickAsync(400);
            await waitForMicrotasks();

            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length,
                2,
                'expected stop refresh to stop retrying after apphost disappears'
            );
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh retries when debug session reports workspace folder path', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceAppHostPath = '/workspace/apps/Store/AppHost.csproj';
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: workspaceAppHostPath,
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(initialFollowCall);
            initialFollowCall.options.lineCallback(JSON.stringify({
                appHostPath: workspaceAppHostPath,
                appHostPid: 1234,
                status: 'running',
            }));
            await waitForCondition(() => repository.workspaceAppHost?.appHostPath === workspaceAppHostPath, 'workspace apphost did not become running');

            repository.requestAppHostStopRefresh('/workspace');
            await clock.tickAsync(400);
            await waitForMicrotasks();

            const snapshotArgs = JSON.stringify(['ps', '--format', 'json', '--nologo']);
            const firstSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(firstSnapshot);
            firstSnapshot.options.stdoutCallback(JSON.stringify([{
                appHostPath: workspaceAppHostPath,
                appHostPid: 1234,
                status: 'running',
            }]));
            firstSnapshot.options.exitCallback(0);
            await waitForMicrotasks();

            await clock.tickAsync(400);
            await waitForMicrotasks();

            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length,
                2,
                'expected retry snapshot while the folder-matched apphost is still running'
            );
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh clears snapshot-running AppHost without restarting follow', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const followArgs = JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']);
            const snapshotArgs = JSON.stringify(['ps', '--format', 'json', '--nologo']);

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === followArgs);
            assert.ok(initialFollowCall, 'expected initial ps --follow call');
            initialFollowCall.options.lineCallback(JSON.stringify({
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
            }));
            await waitForCondition(() => repository.workspaceAppHost?.appHostPath === '/workspace/AppHost.csproj', 'workspace apphost did not become running');

            repository.requestAppHostStopRefresh('/workspace/AppHost.csproj');
            clock.tick(400);
            await waitForMicrotasks();

            const snapshotCall = spawned.find(call => JSON.stringify(call.args) === snapshotArgs);
            assert.ok(snapshotCall, 'expected authoritative snapshot call');
            snapshotCall.options.stdoutCallback('[]');
            snapshotCall.options.exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === followArgs).length,
                1,
                'expected stop refresh to keep existing follow session'
            );

            assert.strictEqual(repository.workspaceAppHost, undefined);
            assert.strictEqual(repository.appHosts.length, 0);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh schedules independent snapshots per apphost path', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const snapshotArgs = JSON.stringify(['ps', '--format', 'json', '--nologo']);
            repository.requestAppHostStopRefresh('/workspace/Store/AppHost.csproj');
            repository.requestAppHostStopRefresh('/workspace/Billing/AppHost.csproj');
            await clock.tickAsync(400);
            await waitForMicrotasks();

            const firstSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(firstSnapshot, 'expected first stop refresh snapshot');
            firstSnapshot.options.stdoutCallback('[]');
            firstSnapshot.options.exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(
                spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length,
                2,
                'expected one snapshot per apphost stop refresh request'
            );
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
        }
    });

    test('stop refresh keeps interval polling active when ps follow is unsupported', async () => {
        const clock = sinon.useFakeTimers();
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoveryService = {
            discover: async () => [{
                path: '/workspace/AppHost.csproj',
                language: 'csharp' as const,
                status: 'buildable' as const,
                selected: true,
            }],
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            dispose: () => { },
        } as unknown as AppHostDiscoveryService;
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });

        const repository = new AppHostDataRepository(terminalProvider, discoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const initialFollowCall = spawned.find(call => JSON.stringify(call.args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']));
            assert.ok(initialFollowCall);

            initialFollowCall.options.errorCallback(new Error('spawn ENOENT'));
            await waitForMicrotasks();

            const snapshotArgs = JSON.stringify(['ps', '--format', 'json', '--nologo']);
            const followArgs = JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo']);
            const snapshotCallsAfterFallback = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length;
            assert.strictEqual(snapshotCallsAfterFallback, 1);
            const fallbackSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(fallbackSnapshot);
            fallbackSnapshot.options.stdoutCallback('[]');
            fallbackSnapshot.options.exitCallback(0);
            await waitForMicrotasks();

            repository.requestAppHostStopRefresh('/workspace/AppHost.csproj');
            await clock.tickAsync(400);
            await waitForMicrotasks();

            assert.strictEqual(spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length, snapshotCallsAfterFallback + 1);
            const postStopSnapshot = spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).at(-1);
            assert.ok(postStopSnapshot);
            postStopSnapshot.options.stdoutCallback('[]');
            postStopSnapshot.options.exitCallback(0);
            await waitForMicrotasks();
            assert.strictEqual(spawned.filter(call => JSON.stringify(call.args) === followArgs).length, 1);

            await clock.tickAsync(30000);
            await waitForMicrotasks();

            assert.strictEqual(spawned.filter(call => JSON.stringify(call.args) === snapshotArgs).length, snapshotCallsAfterFallback + 2);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
            clock.restore();
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
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo'])));
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('visible panel keeps workspace view when workspace has multiple AppHosts and one is selected', async () => {
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

            // Describe is ps-gated: it only streams once ps confirms the selected AppHost running.
            // `open: false` because the host is already the selected discovery candidate — an extra
            // open tab would change which hosts surface in the workspace view.
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 125880,
                cliPid: 125737,
                dashboardUrl: 'https://localhost:17192/login?t=061211',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/apps/Store/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/apps/Store/AppHost.csproj`);
            const describeCall = foundDescribeCall!;

            // The selected host's describe stream stays alive (the view does not tear it down).
            assert.strictEqual((describeCall.returnValue as TestChildProcess).killed, false);
            const spawnArgs = spawnStub.getCalls().map(call => call.args[2] as string[]);
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/apps/Store/AppHost.csproj'])));
            assert.ok(spawnArgs.some(args => JSON.stringify(args) === JSON.stringify(['ps', '--follow', '--format', 'json', '--nologo'])));
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('every running AppHost in workspace gets resources from its own per-AppHost describe stream', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        spawnStub.callsFake((_terminalProvider: any, _command: any, args: string[], options: any) => {
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

            getAppHostsLineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await waitForAppHostDiscovery();

            const describeCount = () => spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe').length;
            // Describe is ps-gated: nothing streams until ps confirms a host running.
            assert.strictEqual(describeCount(), 0);

            // Report both candidates running; `open: false` keeps this a pure discovery scenario. The
            // helper feeds the multi-host ps line and returns the selected host's describe stream.
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([
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
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/apps/Store/AppHost.csproj';
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for /workspace/apps/Store/AppHost.csproj`);
            const selectedDescribe = foundDescribeCall!;

            // Per-host describe streams are equal peers: one spawns for every running AppHost,
            // including the selected/workspace host — there is no special-casing of "non-selected".
            await waitForCondition(() => describeCount() >= 2, 'per-host describe streams should start for both running AppHosts');
            assert.strictEqual(describeCount(), 2);
            const nonSelectedDescribe = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/samples/Store/AppHost.csproj';
            });
            assert.ok(nonSelectedDescribe);

            // Resource data arrives on each host's own describe stream (NDJSON format).
            selectedDescribe.args[3].lineCallback(JSON.stringify({ name: 'api', resourceType: 'Project', state: 'Running' }));
            nonSelectedDescribe.args[3].lineCallback(JSON.stringify({ name: 'redis', resourceType: 'Container', state: 'Running' }));
            await waitForMicrotasks();

            // The selected/workspace host's resources come from its own per-AppHost stream — the
            // workspaceResources getter simply reads them off the selected host.
            const selectedAppHost = repository.appHosts.find((a: any) => a.appHostPath === '/workspace/apps/Store/AppHost.csproj');
            assert.ok(selectedAppHost);
            assert.ok(selectedAppHost.resources);
            assert.strictEqual(selectedAppHost.resources!.length, 1);
            assert.strictEqual(selectedAppHost.resources![0].name, 'api');
            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceResources[0].name, 'api');

            // The non-selected host is populated the same way, from its own stream.
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
            spawnStub.callsFake((_terminalProvider, _command, args, options) => {
                if (args[0] === 'ls') {
                    getAppHostsLineCallback = createLsLineCallback(options);
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
                () => repository?.workspaceAppHostPath === configuredAppHostPath && spawnStub.getCalls().some(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'ps' && spawnArgs.includes('--follow');
                }),
                'configured AppHost discovery did not finish');

            assert.strictEqual(repository.viewMode, 'workspace');
            assert.strictEqual(repository.workspaceAppHostPath, configuredAppHostPath);

            // Describe is ps-gated: the configured selection streams only once ps confirms it running.
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: configuredAppHostPath,
                appHostPid: 125881,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let describeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                describeCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'describe');
                return describeCall !== undefined;
            }, 'expected a describe stream to start');

            assert.strictEqual(repository.workspaceAppHost?.appHostPath, configuredAppHostPath);
            assert.deepStrictEqual(describeCall!.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', configuredAppHostPath]);
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
            assert.strictEqual(repository.workspaceAppHostDescription, 'Workspace view selected because aspire ls found one buildable C# AppHost.');
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
            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ls', '--format', 'json', '--nologo']);

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

    test('empty window republishes cleared workspace loading context on activate', async () => {
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.setPanelVisible(true);
            await vscode.commands.executeCommand('setContext', 'aspire.loading', true);
            repository.activate();
            await waitForMicrotasks();

            assert.strictEqual(repository.isLoading, false);
            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
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

    test('workspace discovery with no buildable AppHosts clears the selected AppHost but keeps a still-running in-workspace host visible', async () => {
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

            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPid, 125881);
            assert.strictEqual(repository.workspaceAppHost, undefined);
            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, true);
            const noAppHostContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noAppHosts');
            assert.strictEqual(noAppHostContextCalls.at(-1)?.args[2], false);
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
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
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

            discovery.resolve([{
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]);
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

    test('workspace ps ignores running AppHosts outside workspace before discovery completes', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
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

    test('switching from global to workspace view in an empty window clears global AppHosts', async () => {
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps') {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForCondition(() => psOptions !== undefined, 'global ps watch did not start');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/other/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
            ]));

            assert.strictEqual(repository.appHosts.length, 1);

            repository.setViewMode('workspace');

            assert.strictEqual(repository.appHosts.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
        } finally {
            repository.dispose();
        }
    });

    test('switching from global to workspace view filters AppHosts to the open workspace before discovery completes', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
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
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForCondition(() => psOptions !== undefined, 'global ps watch did not start');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
                {
                    appHostPath: '/other/apps/Store/AppHost.csproj',
                    appHostPid: 225881,
                    dashboardUrl: 'https://localhost:27193/login?t=061212',
                },
            ]));

            assert.strictEqual(repository.appHosts.length, 2);

            repository.setViewMode('workspace');

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('switching from global to workspace view shows running AppHosts inside the workspace', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        // No discovery service is injected: workspace-view filtering here is driven purely by
        // workspace-folder membership (`isPathInWorkspace`), not by discovered candidate paths.
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ps' && (args as string[]).includes('--follow')) {
                psOptions = options;
            }
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            repository.setViewMode('global');
            await waitForCondition(() => psOptions !== undefined, 'global ps watch did not start');

            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/apps/Store/AppHost.csproj',
                    appHostPid: 125881,
                    dashboardUrl: 'https://localhost:17193/login?t=061212',
                },
                {
                    appHostPath: '/not/workspace/apps/Other/AppHost.csproj',
                    appHostPid: 225881,
                    dashboardUrl: 'https://localhost:27193/login?t=061212',
                },
            ]));

            // Global view lists every running AppHost regardless of workspace membership.
            assert.strictEqual(repository.appHosts.length, 2);

            repository.setViewMode('workspace');

            // Workspace view keeps only AppHosts under a workspace folder, so the out-of-workspace
            // host is filtered out — and the single in-workspace host is adopted as the selected one.
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, '/workspace/apps/Store/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
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
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
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
            await waitForCondition(() => psOptions !== undefined, 'workspace ps watch did not start before discovery completed');

            psOptions.lineCallback(JSON.stringify([]));

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.isLoading, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace loading clears when only an open-tab AppHost outside the folder is running and stays cleared after its tab closes', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        // Never-resolving discovery keeps `discoveryPending` true for the duration of the test, which is
        // the condition under which the old gate refused to clear loading for open-tab-only hosts.
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);

            const outsideAppHostPath = '/other/aspnetcore/aspnetcore.AppHost.csproj';
            repository.setAppHostFilesOpen([outsideAppHostPath]);
            await waitForMicrotasks();
            const psCalls = spawnStub.getCalls().filter(call =>
                (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
            const psCall = psCalls[psCalls.length - 1];
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: outsideAppHostPath,
                appHostPid: 1,
                dashboardUrl: 'https://localhost:17193/login?t=token',
            }]));
            // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
            // primary host's stream by its trailing --apphost argument rather than trusting call order.
            let foundDescribeCall: sinon.SinonSpyCall | undefined;
            await waitForCondition(() => {
                foundDescribeCall = spawnStub.getCalls().find(call => {
                    const spawnArgs = call.args[2] as string[];
                    return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === outsideAppHostPath;
                });
                return foundDescribeCall !== undefined;
            }, `expected a describe stream for ${outsideAppHostPath}`);

            // The host is displayed and discovery is still pending, yet loading must already be cleared
            // because we are showing a host — there is nothing left to search for.
            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, false);
            assert.strictEqual(repository.isLoading, false);
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.appHosts[0].appHostPath, outsideAppHostPath);

            const loadingContextCallsWhileOpen = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCallsWhileOpen.at(-1)?.args[2], false);

            // Close the only open tab. The host is pruned, the tree empties, and loading must stay
            // cleared so the spinner never resurfaces.
            repository.setAppHostFilesOpen([]);
            await waitForMicrotasks();

            assert.strictEqual(repository.appHosts.length, 0);
            assert.strictEqual(repository.isLoading, false);

            const loadingContextCallsAfterClose = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCallsAfterClose.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace loading clears immediately after discovery resolves a single buildable AppHost without a ps change', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        // Discovery resolves a single buildable candidate. Crucially, no `aspire ps` snapshot is fed
        // afterwards: ps `--follow` only emits on a change, so when the running set is already stable
        // the workspace view must clear its "Searching…" loading state the moment discovery resolves —
        // not linger until a ps change that never arrives (the toggle-to-global-and-back workaround).
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => [
                {
                    path: '/workspace/apps/Store/Store.AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                },
            ],
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            assert.strictEqual(repository.isWorkspaceAppHostDiscoveryComplete, true);
            assert.strictEqual(repository.workspaceAppHostPath, '/workspace/apps/Store/Store.AppHost.csproj');
            // No ps snapshot was fed, so nothing is running in the view...
            assert.strictEqual(repository.appHosts.length, 0);
            // ...yet loading must be cleared because discovery resolved a candidate to display.
            assert.strictEqual(repository.isLoading, false);

            const loadingContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.loading');
            assert.strictEqual(loadingContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace ps keeps running after discovery completes with no buildable AppHosts', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => discovery.promise,
            dispose: () => { },
        };
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

            discovery.resolve([{
                path: '/workspace/apps/Store/Store.csproj',
                language: 'csharp',
                status: 'possibly-unbuildable',
            }]);
            await waitForCondition(() => repository.isWorkspaceAppHostDiscoveryComplete, 'workspace discovery did not complete');

            assert.strictEqual(psProcess?.killed, false);
        } finally {
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
        const firstDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const secondDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const discoverStub = sinon.stub();
        discoverStub.onFirstCall().returns(firstDiscovery.promise);
        discoverStub.onSecondCall().returns(secondDiscovery.promise);
        const appHostDiscoveryService = {
            onDidChangeCandidates: discoveryChanges.event,
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStub.callCount, 1);

            discoveryChanges.fire(workspaceFolder);
            discoveryChanges.fire(workspaceFolder);
            discoveryChanges.fire(workspaceFolder);
            await waitForMicrotasks();

            assert.strictEqual(discoverStub.callCount, 1);

            firstDiscovery.resolve([]);
            await waitForCondition(() => discoverStub.callCount === 2, 'pending workspace discovery did not run');
            secondDiscovery.resolve([]);
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStub.callCount, 2);
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
        const firstDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const secondDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        let firstTokenCancelled = false;
        const discoverStub = sinon.stub();
        discoverStub.onFirstCall().callsFake((_folder: vscode.WorkspaceFolder, _forceRefresh?: boolean, cancellationToken?: vscode.CancellationToken) => {
            cancellationToken?.onCancellationRequested(() => {
                firstTokenCancelled = true;
            });
            return firstDiscovery.promise;
        });
        discoverStub.onSecondCall().callsFake((_folder: vscode.WorkspaceFolder, forceRefresh?: boolean) => {
            assert.strictEqual(forceRefresh, true);
            return secondDiscovery.promise;
        });
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStub.callCount, 1);

            repository.refresh();
            await waitForMicrotasks();

            assert.strictEqual(firstTokenCancelled, false);
            assert.strictEqual(discoverStub.callCount, 1);

            firstDiscovery.resolve([]);
            await waitForCondition(() => discoverStub.callCount === 2, 'queued forced workspace discovery did not run');
            secondDiscovery.resolve([]);
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStub.callCount, 2);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('runtime state refresh does not force workspace AppHost rediscovery', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoverStub = sinon.stub().resolves([{
            path: '/workspace/AppHost.csproj',
            language: 'csharp',
            status: 'buildable',
        }]);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStub.callCount, 1);
            spawnStub.resetHistory();

            repository.refreshRuntimeState();
            await waitForMicrotasks();

            assert.strictEqual(discoverStub.callCount, 1);
            const spawnedCalls = spawnStub.getCalls();
            const spawnedArgs = spawnedCalls.map(call => call.args[2] as string[]);
            assert.ok(spawnedArgs.some(args => args[0] === 'ps' && args.includes('--format') && args.includes('json')), 'expected runtime state refresh to refresh running AppHosts');
            assert.ok(spawnedArgs.every(args => args[0] !== 'ls' && args[0] !== 'extension'), 'runtime state refresh should not rediscover workspace AppHosts');
            const snapshotCall = spawnedCalls.find(call => {
                const args = call.args[2] as string[];
                return args[0] === 'ps' && !args.includes('--follow');
            });
            assert.ok(snapshotCall, 'expected runtime state refresh to request a one-shot ps snapshot');
            snapshotCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 125881,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));
            snapshotCall.args[3].exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('runtime state refresh is no-op when repository data sources are inactive', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => {
                throw new Error('aspire ls failed');
            },
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            await waitForAppHostDiscovery();

            assert.match(repository.errorMessage ?? '', /aspire ls failed/);

            repository.refreshRuntimeState();
            await waitForMicrotasks();

            assert.match(repository.errorMessage ?? '', /aspire ls failed/);
            assert.strictEqual(spawnStub.called, false);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('runtime state refresh uses ps fallback when active workspace has no discovered candidate', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoverStub = sinon.stub().resolves([]);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            assert.strictEqual(discoverStub.callCount, 1);
            spawnStub.resetHistory();

            repository.refreshRuntimeState();
            await waitForMicrotasks();

            assert.strictEqual(discoverStub.callCount, 1);
            const spawnedCalls = spawnStub.getCalls();
            const spawnedArgs = spawnedCalls.map(call => call.args[2] as string[]);
            assert.ok(spawnedArgs.some(args => args[0] === 'ps' && args.includes('--format') && args.includes('json')), 'expected runtime state refresh to refresh running AppHosts');
            assert.ok(spawnedArgs.every(args => args[0] !== 'ls' && args[0] !== 'extension'), 'runtime state refresh should not rediscover workspace AppHosts');
            const snapshotCall = spawnedCalls.find(call => {
                const args = call.args[2] as string[];
                return args[0] === 'ps' && !args.includes('--follow');
            });
            assert.ok(snapshotCall, 'expected runtime state refresh to request a one-shot ps snapshot');
            snapshotCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 125881,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));
            snapshotCall.args[3].exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('runtime state refresh while discovery is pending replays ps fallback after empty discovery', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const discoverStub = sinon.stub().returns(discovery.promise);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const spawned: { args: string[]; options: any }[] = [];
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            spawned.push({ args, options });
            return new TestChildProcess();
        });
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForCondition(() => spawned.some(call => call.args[0] === 'ps' && call.args.includes('--follow')), 'workspace ps watch did not start before discovery completed');

            const initialSpawnCount = spawned.length;
            repository.refreshRuntimeState();
            await waitForMicrotasks();

            assert.strictEqual(discoverStub.callCount, 1);
            assert.ok(spawned.slice(initialSpawnCount).some(call => call.args[0] === 'ps' && !call.args.includes('--follow')), 'expected pending runtime refresh to request an immediate ps snapshot');

            const initialSnapshotCall = spawned.filter(call => call.args[0] === 'ps' && !call.args.includes('--follow')).at(-1);
            assert.ok(initialSnapshotCall, 'expected runtime state refresh to request an immediate ps snapshot');
            initialSnapshotCall.options.stdoutCallback(JSON.stringify([]));
            initialSnapshotCall.options.exitCallback(0);
            await waitForMicrotasks();

            discovery.resolve([]);
            await waitForCondition(() => repository.isWorkspaceAppHostDiscoveryComplete, 'workspace discovery did not complete');
            await waitForCondition(
                () => spawned.slice(initialSpawnCount).filter(call => call.args[0] === 'ps' && !call.args.includes('--follow')).length >= 2,
                'runtime refresh did not replay forced ps fallback after empty discovery'
            );

            const snapshotCall = spawned.filter(call => call.args[0] === 'ps' && !call.args.includes('--follow')).at(-1);
            assert.ok(snapshotCall, 'expected runtime state refresh to request a forced ps snapshot');
            snapshotCall.options.stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 125881,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));
            snapshotCall.options.exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(discoverStub.callCount, 1);
            assert.strictEqual(repository.appHosts.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/AppHost.csproj');
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.ok(spawned.slice(initialSpawnCount).every(call => call.args[0] !== 'ls' && call.args[0] !== 'extension'), 'runtime state refresh should not rediscover workspace AppHosts');
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('forced runtime state refresh does not spawn ps after becoming inactive', async () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        const workspaceFoldersStub = stubWorkspaceFolders([workspaceFolder]);
        const discoverStub = sinon.stub().resolves([]);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForAppHostDiscovery();

            const cliPath = createDeferred<string>();
            getCliPathStub.resetBehavior();
            getCliPathStub.returns(cliPath.promise);
            spawnStub.resetHistory();

            repository.refreshRuntimeState();
            await waitForMicrotasks();
            repository.setPanelVisible(false);
            await waitForMicrotasks();

            cliPath.resolve('aspire');
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.called, false);
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
        const firstDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const secondDiscovery = createDeferred<CandidateAppHostDisplayInfo[]>();
        const discoverStub = sinon.stub();
        discoverStub.onFirstCall().returns(firstDiscovery.promise);
        discoverStub.onSecondCall().returns(secondDiscovery.promise);
        const appHostDiscoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: discoverStub,
            dispose: () => { },
        };
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            await waitForMicrotasks();
            assert.strictEqual(discoverStub.callCount, 1);

            repository.refresh();
            await waitForMicrotasks();
            assert.strictEqual(discoverStub.callCount, 1);

            firstDiscovery.resolve([{
                path: '/workspace/stale/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]);
            await waitForCondition(() => discoverStub.callCount === 2, 'queued forced workspace discovery did not run');

            assert.strictEqual(repository.workspaceAppHostPath, undefined);

            secondDiscovery.resolve([{
                path: '/workspace/current/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            }]);
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
            await waitForCondition(() => spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps').some(call => !(call.args[2] as string[]).includes('--follow')), 'ps fallback did not start');

            const psFallbackCall = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'ps').find(call => !(call.args[2] as string[]).includes('--follow'));
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

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], false);
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

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], false);
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

            const compatibilityContextCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.fetchAppHostsCompatibilityError');
            assert.strictEqual(compatibilityContextCalls.at(-1)?.args[2], false);
        } finally {
            repository.dispose();
            executeCommandStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('closing the last AppHost tab while the panel is hidden removes the still-running host on reopen', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();
        const psCalls = spawnStub.getCalls().filter(call =>
            (call.args[2] as string[])[0] === 'ps' && (call.args[2] as string[]).includes('--follow'));
        const psCall = psCalls[psCalls.length - 1];
        assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
        psCall.args[3].lineCallback(JSON.stringify([{
            appHostPath: '/workspace/AppHost.csproj',
            appHostPid: 1,
            dashboardUrl: 'https://localhost:17193/login?t=token',
        }]));
        // Every running host spawns its own `describe --follow --apphost <path>` stream, so match the
        // primary host's stream by its trailing --apphost argument rather than trusting call order.
        let foundDescribeCall: sinon.SinonSpyCall | undefined;
        await waitForCondition(() => {
            foundDescribeCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'describe' && spawnArgs[spawnArgs.length - 1] === '/workspace/AppHost.csproj';
            });
            return foundDescribeCall !== undefined;
        }, `expected a describe stream for /workspace/AppHost.csproj`);
        assert.strictEqual(repository.appHosts.length, 1);

        repository.setPanelVisible(false);
        await waitForMicrotasks();

        repository.setAppHostFilesOpen([]);
        await waitForMicrotasks();

        assert.deepStrictEqual(repository.appHosts, []);

        repository.setPanelVisible(true);
        await waitForMicrotasks();

        assert.deepStrictEqual(repository.appHosts, []);

        repository.dispose();
    });

    test('hiding workspace panel clears workspace resources', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let describeOptions: any;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions = options;
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
                selected_project_file: '/workspace/AppHost.csproj',
                all_project_file_candidates: ['/workspace/AppHost.csproj'],
            }));
            await waitForAppHostDiscovery();

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([{ appHostPath: '/workspace/AppHost.csproj', appHostPid: 1 }]));
            await waitForMicrotasks();

            assert.ok(describeOptions);
            describeOptions.lineCallback(JSON.stringify({ name: 'api' }));

            assert.strictEqual(repository.workspaceResources.length, 1);

            repository.setPanelVisible(false);

            assert.strictEqual(repository.workspaceResources.length, 0);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('hiding workspace panel before cli path resolves prevents describe from starting', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const discoveryEmitter = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        const appHostDiscoveryService = {
            onDidChangeCandidates: discoveryEmitter.event,
            discover: () => new Promise<CandidateAppHostDisplayInfo[]>(() => { }),
            dispose: () => { },
        };
        const describeCliPath = createDeferred<string>();
        getCliPathStub.onFirstCall().resolves('aspire');
        getCliPathStub.onSecondCall().returns(describeCliPath.promise);
        const repository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService as unknown as AppHostDiscoveryService);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const psCall = spawnStub.getCalls().find(call => {
                const args = call.args[2] as string[];
                return args[0] === 'ps' && args.includes('--follow');
            });
            assert.ok(psCall, 'expected an aspire ps --follow watch to be running');
            psCall.args[3].lineCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1,
            }]));
            await waitForMicrotasks();

            assert.strictEqual(getCliPathStub.callCount, 2);
            assert.strictEqual(spawnStub.getCalls().some(call => (call.args[2] as string[])[0] === 'describe'), false);

            repository.setPanelVisible(false);
            describeCliPath.resolve('aspire');
            await waitForMicrotasks();

            assert.strictEqual(spawnStub.getCalls().some(call => (call.args[2] as string[])[0] === 'describe'), false);
        } finally {
            repository.dispose();
            discoveryEmitter.dispose();
            workspaceFoldersStub.restore();
        }
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
            assert.deepStrictEqual(psArgs, ['ps', '--follow', '--format', 'json', '--nologo']);
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

    test('workspace describe dashboard URL shows dashboard command after ps reports the AppHost', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        let describeOptions: any;
        let psOptions: any;
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions = options;
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
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: ['/workspace/apps/Store/AppHost.csproj'],
            }));
            await waitForAppHostDiscovery();

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([{
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 125881,
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));
            await waitForMicrotasks();

            assert.ok(describeOptions);
            describeOptions.lineCallback(JSON.stringify({
                name: 'api',
                resourceType: 'Project',
                state: 'Running',
                dashboardUrl: 'http://localhost:18888/resource/api',
            }));

            assert.strictEqual(repository.workspaceResources.length, 1);

            const noLiveAppHostsCalls = executeCommandStub.getCalls().filter(call =>
                call.args[0] === 'setContext' && call.args[1] === 'aspire.noRunningAppHosts');
            assert.strictEqual(noLiveAppHostsCalls.at(-1)?.args[2], false);
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

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));
            await waitForMicrotasks();

            assert.ok(describeOptions);
            describeOptions.lineCallback(JSON.stringify({ name: 'worker', resourceType: 'Project', state: 'Running' }));

            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPath, '/workspace/labs/ops/apphost.cs');

            psOptions.lineCallback(JSON.stringify([]));
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(repository.workspaceAppHost, undefined);
            assert.strictEqual(describeProcess.killed, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('workspace describe exit confirms host removal before ps polling reports it', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        let getAppHostsLineCallback: ((line: string) => void) | undefined;
        const describeProcess = new TestChildProcess();
        let describeOptions: any;
        let psFollowOptions: any;
        const oneShotPsOptions: any[] = [];
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            if (args[0] === 'ls') {
                getAppHostsLineCallback = createLsLineCallback(options);
            }
            if (args[0] === 'describe') {
                describeOptions = options;
                return describeProcess;
            }
            if (args[0] === 'ps') {
                if (args.includes('--follow')) {
                    psFollowOptions = options;
                } else {
                    oneShotPsOptions.push(options);
                }
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

            assert.ok(psFollowOptions);
            // Describe is ps-gated: it spawns only once ps confirms the selected host running.
            psFollowOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));
            await waitForMicrotasks();

            assert.ok(describeOptions);
            describeOptions.lineCallback(JSON.stringify({ name: 'worker', resourceType: 'Project', state: 'Running' }));

            assert.strictEqual(repository.workspaceResources.length, 1);
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(repository.appHosts.length, 1);

            const oneShotBeforeExit = oneShotPsOptions.length;
            describeOptions.exitCallback(0);
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(repository.appHosts.length, 1);

            // The exit triggered an authoritative snapshot (`ps --format json`, not `--follow`).
            assert.ok(oneShotPsOptions.length > oneShotBeforeExit, 'expected describe exit to trigger a ps snapshot');
            const confirmSnapshot = oneShotPsOptions.at(-1);

            // The snapshot reports the host gone (it was stopped externally): it is removed promptly, without
            // waiting for the `ps --follow` stream to emit a stop delta.
            confirmSnapshot.stdoutCallback('[]');
            confirmSnapshot.exitCallback(0);
            await waitForMicrotasks();

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
            if (args[0] === 'ps' && args.includes('--follow')) {
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

            // Describe is ps-gated: discovery alone spawns no describe stream.
            assert.strictEqual(describeOptions.length, 0);

            assert.ok(psOptions);
            psOptions.lineCallback(JSON.stringify([
                {
                    appHostPath: '/workspace/labs/ops/apphost.cs',
                    appHostPid: 125881,
                },
            ]));
            await waitForMicrotasks();

            assert.strictEqual(repository.workspaceAppHost?.appHostPid, 125881);
            assert.strictEqual(describeOptions.length, 1);

            // First describe exits empty while ps still reports the host running. This clears the stale
            // resources but keeps the stream entry (a backoff restart is scheduled, not advanced here) — the
            // host stays until ps says otherwise.
            describeOptions[0].exitCallback(0);
            assert.strictEqual(repository.workspaceResources.length, 0);

            // ps --follow drives the restart: the stopped delta tears down the first stream (clearing its
            // pending restart timer) and the running-again delta reconciles into a fresh describe. This guards
            // that an earlier empty exit doesn't poison a later ps-driven restart.
            psOptions.lineCallback(JSON.stringify([]));
            await waitForMicrotasks();
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
            // The first stream self-exited (never force-killed); the second is the active replacement.
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

            // Describe is ps-gated: discovery alone spawns no describe and raises no error.
            assert.strictEqual(describeOptions.length, 0);
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
            assert.strictEqual(describeOptions.length, 1);

            // A running, selected AppHost whose describe exits(0) without producing data is treated
            // as a version-compatibility signal (the AppHost is too old to support `describe`).
            describeOptions[0].exitCallback(0);

            assert.strictEqual(repository.hasError, true);
            assert.ok(repository.errorMessage?.includes('Aspire.Hosting 13.2.0'), repository.errorMessage);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('late close from stopped describe does not orphan replacement', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const firstChildProcess = new TestChildProcess();
        const secondChildProcess = new TestChildProcess();
        const describeProcesses = [firstChildProcess, secondChildProcess];
        spawnStub.callsFake((_terminalProvider, _command, args) =>
            args[0] === 'describe' ? describeProcesses.shift()! : new TestChildProcess());
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            const firstDescribeCall = await startDescribeForRunningAppHost(repository, '/workspace/AppHost.csproj');
            const firstLineCallback = firstDescribeCall.args[3].lineCallback;
            const firstExitCallback = firstDescribeCall.args[3].exitCallback;

            repository.setPanelVisible(false);
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const describeCalls = spawnStub.getCalls().filter(call => (call.args[2] as string[])[0] === 'describe');
            assert.strictEqual(describeCalls.length, 2);

            firstLineCallback(JSON.stringify({ name: 'stale' }));
            firstExitCallback(0);
            repository.setPanelVisible(false);

            assert.strictEqual(repository.workspaceResources.length, 0);
            assert.strictEqual(firstChildProcess.killed, true);
            assert.strictEqual(secondChildProcess.killed, true);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('stubborn describe is force killed', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const clock = sinon.useFakeTimers();
        const childProcess = new TestChildProcess(false);
        spawnStub.callsFake((_terminalProvider, _command, args) =>
            args[0] === 'describe' ? childProcess : new TestChildProcess());
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            await startDescribeForRunningAppHost(repository, '/workspace/AppHost.csproj');

            repository.setPanelVisible(false);
            clock.tick(5000);

            assert.deepStrictEqual(childProcess.killSignals, [undefined, 'SIGKILL']);
        } finally {
            repository.dispose();
            clock.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('Windows describe termination uses taskkill for the process tree', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const spawnProcessStub = sinon.stub(nodeChildProcess, 'spawn');
        const clock = sinon.useFakeTimers();
        const childProcess = new TestChildProcess(false);
        Object.defineProperty(childProcess, 'pid', { value: 4242 });
        spawnStub.callsFake((_terminalProvider, _command, args) =>
            args[0] === 'describe' ? childProcess : new TestChildProcess());
        const taskkillCalls: Array<{ command: string; args: string[]; windowsHide: boolean | undefined }> = [];
        spawnProcessStub.callsFake((command: string, args?: readonly string[], options?: nodeChildProcess.SpawnOptions) => {
            taskkillCalls.push({
                command,
                args: [...(args ?? [])],
                windowsHide: options?.windowsHide,
            });

            return Object.assign(new EventEmitter(), {
                unref: () => { },
            }) as nodeChildProcess.ChildProcess;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            await startDescribeForRunningAppHost(repository, '/workspace/AppHost.csproj');

            repository.setPanelVisible(false);
            assert.deepStrictEqual(taskkillCalls, [{
                command: 'taskkill.exe',
                args: ['/pid', '4242', '/t'],
                windowsHide: true,
            }]);
            assert.deepStrictEqual(childProcess.killSignals, []);

            await clock.tickAsync(5000);

            assert.deepStrictEqual(taskkillCalls, [
                {
                    command: 'taskkill.exe',
                    args: ['/pid', '4242', '/t'],
                    windowsHide: true,
                },
                {
                    command: 'taskkill.exe',
                    args: ['/pid', '4242', '/t', '/f'],
                    windowsHide: true,
                },
            ]);
        } finally {
            repository.dispose();
            clock.restore();
            spawnProcessStub.restore();
            platformStub.restore();
            workspaceFoldersStub.restore();
        }
    });

    test('already-exited describe is not terminated again', async () => {
        const workspaceFoldersStub = stubWorkspaceFolders([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        const childProcess = new TestChildProcess();
        spawnStub.callsFake((_terminalProvider, _command, args) =>
            args[0] === 'describe' ? childProcess : new TestChildProcess());
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setPanelVisible(true);
            await waitForMicrotasks();
            await startDescribeForRunningAppHost(repository, '/workspace/AppHost.csproj');
            childProcess.markExited();

            repository.setPanelVisible(false);

            assert.strictEqual(childProcess.killed, false);
            assert.strictEqual(childProcess.listenerCount('close'), 0);
            assert.strictEqual(childProcess.listenerCount('exit'), 0);
        } finally {
            repository.dispose();
            workspaceFoldersStub.restore();
        }
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

        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--follow', '--format', 'json', '--nologo']);

        repository.setPanelVisible(false);

        assert.strictEqual(childProcess.killed, true);

        repository.dispose();
    });

    test('switching into global view while ps is already polling clears the loading spinner', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        // Open an AppHost tab so the data sources go active and ps --follow starts in workspace view.
        repository.setAppHostFilesOpen(['/workspace/AppHost.csproj']);
        await waitForMicrotasks();

        const psCall = spawnStub.getCalls().find(call => (call.args[2] as string[])[0] === 'ps');
        assert.ok(psCall, 'expected aspire ps --follow to start for the open AppHost tab');
        const spawnCountBeforeSwitch = spawnStub.callCount;

        repository.setViewMode('global');
        await waitForMicrotasks();

        assert.strictEqual(spawnStub.callCount, spawnCountBeforeSwitch, 'ps must not respawn on view switch');
        assert.strictEqual(repository.isLoading, false, 'global spinner must clear when ps is already polling');

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

        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--follow', '--format', 'json', '--nologo']);

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
        assert.deepStrictEqual(describeCall.args[2], ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

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

    test('global ps follow retries older-CLI nologo rejection from bounded output suffix', async () => {
        const firstProcess = new TestChildProcess();
        const retryProcess = new TestChildProcess();
        spawnStub.onFirstCall().returns(firstProcess);
        spawnStub.onSecondCall().returns(retryProcess);
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--follow', '--format', 'json', '--nologo']);
            spawnStub.firstCall.args[3].stderrCallback('x'.repeat(5000));
            spawnStub.firstCall.args[3].stderrCallback("Unrecognized command or argument '--nologo'.");
            spawnStub.firstCall.args[3].exitCallback(1);
            await waitForMicrotasks();
            await waitForMicrotasks();

            assert.deepStrictEqual(spawnStub.secondCall.args[2], ['ps', '--follow', '--format', 'json']);
        } finally {
            repository.dispose();
        }
    });

    test('global ps without dashboard URL keeps dashboard commands hidden', async () => {
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const repository = new AppHostDataRepository(terminalProvider);
        const getNoRunningAppHostsContext = () => executeCommandStub.getCalls()
            .filter(call => call.args[0] === 'setContext' && call.args[1] === 'aspire.noRunningAppHosts')
            .at(-1)?.args[2];

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const psLineCallback = spawnStub.firstCall.args[3].lineCallback;
            psLineCallback(JSON.stringify({
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
                dashboardUrl: null,
            }));
            await waitForMicrotasks();

            assert.strictEqual(getNoRunningAppHostsContext(), true);

            repository.setPanelVisible(false);
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            assert.strictEqual(getNoRunningAppHostsContext(), true);

            const latestSnapshotCall = spawnStub.getCalls()
                .filter(call =>
                    call.args[2][0] === 'ps'
                    && call.args[2][1] === '--format'
                    && call.args[2][2] === 'json')
                .at(-1);
            assert.ok(latestSnapshotCall);
            latestSnapshotCall.args[3].stdoutCallback(JSON.stringify([{
                appHostPath: '/workspace/AppHost.csproj',
                appHostPid: 1234,
                status: 'running',
                dashboardUrl: 'https://localhost:17193/login?t=061212',
            }]));
            latestSnapshotCall.args[3].exitCallback(0);
            await waitForCondition(
                () => getNoRunningAppHostsContext() === false,
                'running AppHost with dashboard URL did not restore dashboard command context');
        } finally {
            repository.dispose();
            executeCommandStub.restore();
        }
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
        assert.deepStrictEqual(firstDescribe.args, ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/workspace/AppHost.csproj']);

        firstDescribe.options.stderrCallback("Unrecognized command or argument '--include-disabled-commands'");
        firstDescribe.options.exitCallback(1);
        await waitForMicrotasks();

        const describeCalls = spawned.filter(call => call.args[0] === 'describe');
        assert.strictEqual(describeCalls.length, 2);
        assert.deepStrictEqual(describeCalls[1].args, ['describe', '--follow', '--format', 'json', '--nologo', '--apphost', '/workspace/AppHost.csproj']);

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
            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ps', '--format', 'json', '--nologo']);
        } finally {
            repository.dispose();
            clock.restore();
        }
    });

    test('new polling interval setting takes precedence and changing it restarts interval polling', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const configChange = new vscode.EventEmitter<vscode.ConfigurationChangeEvent>();
        const onDidChangeConfigurationStub = sinon.stub(vscode.workspace, 'onDidChangeConfiguration')
            .callsFake(listener => configChange.event(listener));
        const inspect = sinon.stub();
        inspect.withArgs('appHostsPollingInterval').returns({ globalValue: 2000 });
        inspect.withArgs('globalAppHostsPollingInterval').returns({ globalValue: 9000 });
        const getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration');
        getConfigurationStub.withArgs('aspire').returns({
            inspect,
            get: sinon.stub().withArgs('appHostsPollingInterval', 30000).returns(30000),
        } as unknown as vscode.WorkspaceConfiguration);
        getConfigInfoStub.resolves({ capabilities: [] } as any);
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            const process = new TestChildProcess();
            if (args[0] === 'ps' && !args.includes('--follow')) {
                options.stdoutCallback('[]');
                options.exitCallback(0);
                process.markExited();
            }
            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const psFollowCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && spawnArgs.includes('--follow');
            });
            assert.ok(psFollowCall);
            psFollowCall.args[3].exitCallback(1);
            await waitForMicrotasks();

            const initialSpawnCount = spawnStub.callCount;
            await clock.tickAsync(1999);
            assert.strictEqual(spawnStub.callCount, initialSpawnCount);
            await clock.tickAsync(1);
            assert.strictEqual(spawnStub.callCount, initialSpawnCount + 1);

            inspect.withArgs('appHostsPollingInterval').returns({ globalValue: 4000 });
            configChange.fire({ affectsConfiguration: section => section === 'aspire.appHostsPollingInterval' });
            await waitForMicrotasks();
            const restartedSpawnCount = spawnStub.callCount;
            await clock.tickAsync(3999);
            assert.strictEqual(spawnStub.callCount, restartedSpawnCount);
            await clock.tickAsync(1);
            assert.strictEqual(spawnStub.callCount, restartedSpawnCount + 1);
        } finally {
            repository.dispose();
            getConfigurationStub.restore();
            onDidChangeConfigurationStub.restore();
            configChange.dispose();
            clock.restore();
        }
    });

    test('deprecated polling interval setting remains the fallback when the new setting is unset', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const inspect = sinon.stub();
        inspect.withArgs('appHostsPollingInterval').returns({});
        inspect.withArgs('globalAppHostsPollingInterval').returns({ workspaceValue: 2500 });
        const getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration');
        getConfigurationStub.withArgs('aspire').returns({
            inspect,
            get: sinon.stub().withArgs('appHostsPollingInterval', 30000).returns(30000),
        } as unknown as vscode.WorkspaceConfiguration);
        getConfigInfoStub.resolves({ capabilities: [] } as any);
        spawnStub.callsFake((_terminalProvider, _command, args, options) => {
            const process = new TestChildProcess();
            if (args[0] === 'ps' && !args.includes('--follow')) {
                options.stdoutCallback('[]');
                options.exitCallback(0);
                process.markExited();
            }
            return process;
        });
        const repository = new AppHostDataRepository(terminalProvider);

        try {
            repository.activate();
            repository.setViewMode('global');
            repository.setPanelVisible(true);
            await waitForMicrotasks();

            const psFollowCall = spawnStub.getCalls().find(call => {
                const spawnArgs = call.args[2] as string[];
                return spawnArgs[0] === 'ps' && spawnArgs.includes('--follow');
            });
            assert.ok(psFollowCall);
            psFollowCall.args[3].exitCallback(1);
            await waitForMicrotasks();

            const initialSpawnCount = spawnStub.callCount;
            await clock.tickAsync(2499);
            assert.strictEqual(spawnStub.callCount, initialSpawnCount);
            await clock.tickAsync(1);
            assert.strictEqual(spawnStub.callCount, initialSpawnCount + 1);
        } finally {
            repository.dispose();
            getConfigurationStub.restore();
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

suite('AppHostDataRepository open-AppHost-tab describe', () => {
    let terminalProvider: AspireTerminalProvider;
    let subscriptions: vscode.Disposable[];
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;
    let getConfigInfoStub: sinon.SinonStub;
    let workspaceFoldersStub: sinon.SinonStub;
    let spawned: { args: string[]; process: TestChildProcess; options: any }[];

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawned = [];
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        spawnStub.callsFake((_terminalProvider, _cliPath, args, options) => {
            const process = new TestChildProcess();
            spawned.push({ args, process, options });
            return process;
        });
        // Stub the capability probe so the constructor's eager `config info --json` doesn't
        // spawn through spawnCliProcess and pollute these suites' spawn assertions.
        getConfigInfoStub = sinon.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            capabilities: [describeIncludeDisabledCommandsCapability],
        } as any);
        // This suite exercises open editor-tab surfacing with AppHosts outside any workspace folder.
        // Pin `workspaceFolders` to undefined so the scenarios are deterministic regardless of the
        // folder the VS Code test host itself has open, matching the folderless / single-file case.
        workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value(undefined);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        getConfigInfoStub.restore();
        workspaceFoldersStub.restore();
        subscriptions.forEach(subscription => subscription.dispose());
    });

    async function reportRunningAppHost(appHost: { appHostPath: string; appHostPid: number; status?: string }): Promise<void> {
        const psCall = spawned.find(call => call.args[0] === 'ps');
        assert.ok(psCall, 'expected `aspire ps` to be spawned while an AppHost tab is open');
        psCall.options.lineCallback(JSON.stringify({ status: 'running', ...appHost }));
        await waitForMicrotasks();
    }

    function describeCalls(): { args: string[]; process: TestChildProcess; options: any }[] {
        return spawned.filter(call => call.args[0] === 'describe');
    }

    function apphostTargetOf(call: { args: string[] }): string {
        return call.args[call.args.indexOf('--apphost') + 1];
    }

    test('opening a single-file AppHost tab follows the running host reported by aspire ps', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        assert.ok(spawned.some(call => call.args[0] === 'ps'), 'expected `aspire ps` to start for the open tab');
        assert.strictEqual(describeCalls().length, 0, 'describe must not start until ps reports a matching running host');

        await reportRunningAppHost({ appHostPath: '/test/AppHost.cs', appHostPid: 4321 });

        assert.strictEqual(describeCalls().length, 1);
    assert.deepStrictEqual(describeCalls()[0].args, ['describe', '--follow', '--format', 'json', '--nologo', '--include-disabled-commands', '--apphost', '/test/AppHost.cs']);

        repository.dispose();
    });

    test('closing all AppHost files with hidden panel stops aspire ps polling', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        const psCall = spawned.find(call => call.args[0] === 'ps');
        assert.ok(psCall);
        assert.strictEqual(psCall.process.killed, false);

        repository.setAppHostFilesOpen([]);
        await waitForMicrotasks();

        assert.strictEqual(psCall.process.killed, true);

        repository.dispose();
    });

    test('opening a project AppHost source tab describes the ps-reported .csproj, not the open source file', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        await reportRunningAppHost({ appHostPath: '/test/AppHost.csproj', appHostPid: 4321 });

        assert.strictEqual(describeCalls().length, 1);
        assert.strictEqual(apphostTargetOf(describeCalls()[0]), '/test/AppHost.csproj');

        repository.dispose();
    });

    test('aspire ps stays alive and is not respawned when one of several open AppHost tabs is closed', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs', '/other/apphost.ts']);
        await waitForMicrotasks();

        const psCalls = spawned.filter(call => call.args[0] === 'ps');
        assert.strictEqual(psCalls.length, 1);
        assert.strictEqual(psCalls[0].process.killed, false);

        // Closing one tab while another AppHost tab stays open leaves the open set non-empty, so the
        // keep-alive gate must not flip: ps must neither be terminated nor respawned. A flip here is
        // exactly the spawn/force-kill churn this guards against.
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        assert.strictEqual(spawned.filter(call => call.args[0] === 'ps').length, 1, 'ps must not respawn while an AppHost tab remains open');
        assert.strictEqual(psCalls[0].process.killed, false, 'ps must not be terminated while an AppHost tab remains open');

        repository.dispose();
    });

    test('every open AppHost tab is described and surfaces in the workspace panel while the panel is hidden', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs', '/other/apphost.ts']);
        await waitForMicrotasks();
        await reportRunningAppHost({ appHostPath: '/test/AppHost.cs', appHostPid: 4321 });
        await reportRunningAppHost({ appHostPath: '/other/apphost.ts', appHostPid: 8765 });

        const describedTargets = describeCalls().map(apphostTargetOf).sort();
        assert.deepStrictEqual(describedTargets, ['/other/apphost.ts', '/test/AppHost.cs']);
        assert.ok(repository.appHosts.some(appHost => appHost.appHostPath === '/test/AppHost.cs'));
        assert.ok(repository.appHosts.some(appHost => appHost.appHostPath === '/other/apphost.ts'));

        repository.dispose();
    });

    test('closing the AppHost tab stops its describe stream', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();
        await reportRunningAppHost({ appHostPath: '/test/AppHost.cs', appHostPid: 4321 });

        const describeCall = describeCalls()[0];
        assert.ok(describeCall);
        assert.strictEqual(describeCall.process.killed, false);

        repository.setAppHostFilesOpen([]);
        await waitForMicrotasks();

        assert.strictEqual(describeCall.process.killed, true);

        repository.dispose();
    });

    test('reporting the same open AppHost set again does not restart ps polling', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        assert.strictEqual(spawned.filter(call => call.args[0] === 'ps').length, 1);

        repository.dispose();
    });

    test('opening a tab for an already-running host surfaces it and describes it without a refresh', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        // A visible panel with no open AppHost tab keeps `aspire ps --follow` alive, so the host can
        // already be running (and reported) before the user opens its editor tab.
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        // ps reports the host running while nothing targets it: it is retained as a running host but
        // not displayed (not a configured workspace candidate, no open tab).
        await reportRunningAppHost({ appHostPath: '/test/AppHost.cs', appHostPid: 4321 });
        assert.strictEqual(repository.appHosts.length, 0, 'a running host with no open tab must not display in the workspace panel');
        assert.strictEqual(describeCalls().length, 0);

        // Opening the tab must surface the already-running host immediately by re-deriving against the
        // retained running set — `ps --follow` will not re-emit the unchanged host, so requiring a
        // refresh here is the bug this guards against.
        repository.setAppHostFilesOpen(['/test/AppHost.cs']);
        await waitForMicrotasks();

        assert.ok(
            repository.appHosts.some(appHost => appHost.appHostPath === '/test/AppHost.cs'),
            'opening the tab must surface the already-running host without a refresh',
        );
        assert.strictEqual(describeCalls().length, 1);
        assert.strictEqual(apphostTargetOf(describeCalls()[0]), '/test/AppHost.cs');

        repository.dispose();
    });

    test('switching to global mode surfaces a host reported while in workspace mode, without a refresh', async () => {
        const repository = new AppHostDataRepository(terminalProvider);

        repository.activate();
        repository.setPanelVisible(true);
        await waitForMicrotasks();

        await reportRunningAppHost({ appHostPath: '/test/AppHost.cs', appHostPid: 4321 });
        assert.strictEqual(repository.appHosts.length, 0);

        repository.setViewMode('global');
        await waitForMicrotasks();

        assert.ok(
            repository.appHosts.some(appHost => appHost.appHostPath === '/test/AppHost.cs'),
            'global mode must surface the already-running host without a refresh',
        );

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
