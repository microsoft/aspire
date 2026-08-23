import * as assert from 'assert';
import * as childProcess from 'child_process';
import fs = require('fs');
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../utils/process/cliProcess';
import * as cliPathModule from '../utils/cliPath';
import * as configInfoProvider from '../utils/configInfoProvider';
import * as workspaceModule from '../utils/workspace';
import * as appHostIdentityModule from '../utils/appHostIdentity';
import { registerTreeViewCommands } from '../activation/registerTreeViewCommands';
import { AppHostDataRepository, shortenPath, shortenPaths } from '../data/AppHostDataRepository';
import { AspireCliFailedError } from '../data/appHostCliContracts';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { getResourceContextValue, getResourceIcon, getResourceCommandIcon, resolveAppHostSourcePath, buildResourceDescription } from '../views/treePresentation';
import { AppHostItem, WorkspaceAppHostItem, WorkspaceResourcesItem } from '../views/treeItems';
import type { Clipboard } from '../views/AspireAppHostTreeProvider';
import type { AppHostDisplayInfo, ResourceJson, ViewMode } from '../data/AppHostDataRepository';
import { AppHostCliRunner } from '../data/appHostCliRunner';
import { ResourceCommandInputType } from '../data/AppHostDataRepository';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import type { AspireSubcommand } from '../utils/AspireTerminalProvider';
import { AspireTerminalProvider, shellArg } from '../utils/AspireTerminalProvider';
import { AppHostLaunchService, type AppHostOperationState } from '../services/AppHostLaunchService';
import { terminalCommandArgumentControlCharacters, appHostPathCopiedToClipboard, appHostPathInvalid, appHostSourceNotFound, loadingPipelineSteps } from '../loc/strings';
import { onDidInvokeCommand, withCommandTelemetry } from '../utils/telemetry';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import {
    lsJsonStreamCapability,
    pipelineInteractionCapability,
    pipelineStepListJsonCapability,
    type ConfigInfo,
} from '../types/configInfo';
import { windowCliPathTarget, workspaceFolderCliPathTarget, type CliPathResolutionTarget } from '../utils/cliPathVariables';

import { createWorkspaceFolder, removeDirectorySafely } from './testHelpers';
function makeResource(overrides: Partial<ResourceJson> = {}): ResourceJson {
    const base: ResourceJson = {
        name: 'my-service',
        displayName: null,
        resourceType: 'Project',
        state: null,
        stateStyle: null,
        healthStatus: null,
        healthReports: null,
        exitCode: null,
        dashboardUrl: null,
        urls: null,
        commands: null,
        properties: null,
    };
    return { ...base, ...overrides } as ResourceJson;
}

function buildPath(...segments: string[]): string {
    return path.join(...segments);
}

function makeAppHost(overrides: Partial<AppHostDisplayInfo> = {}): AppHostDisplayInfo {
    return {
        appHostPath: '/test/AppHost.csproj',
        appHostPid: 1234,
        cliPid: null,
        dashboardUrl: null,
        resources: null,
        ...overrides,
    };
}

function makeLaunchService(): AppHostLaunchService {
    return new AppHostLaunchService({
        getCapabilityStatus: async () => 'supported',
    });
}

function makeTerminalProvider(): AspireTerminalProvider {
    return {
        resolveAspireCliPath: async () => ({ cliPath: 'aspire', available: true, source: 'path' }),
        getAspireCliExecutablePath: async () => 'aspire',
        createEnvironment: () => ({}),
        sendAspireCommandToAspireTerminal: () => { },
    } as unknown as AspireTerminalProvider;
}

interface FakeClipboard extends Clipboard {
    text: string | undefined;
}

// Deterministic in-memory clipboard so copy actions can be verified without touching the real OS
// clipboard, which is unavailable on headless CI and corrupted by concurrent test execution.
function makeClipboard(): FakeClipboard {
    return {
        text: undefined,
        async writeText(value: string): Promise<void> {
            this.text = value;
        },
    };
}

function makeTreeProvider(appHosts: readonly AppHostDisplayInfo[], viewMode: ViewMode = 'global', workspaceAppHostDescription?: string): AspireAppHostTreeProvider {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    const repository = {
        viewMode,
        appHosts,
        workspaceResources: [],
        workspaceAppHostPath: undefined,
        workspaceAppHostCandidatePaths: [],
        workspaceAppHostName: undefined,
        workspaceAppHostDescription,
        onDidChangeData,
    } as unknown as AppHostDataRepository;

    return new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());
}

function getResourceCommandItems(provider: AspireAppHostTreeProvider): readonly vscode.TreeItem[] {
    const [appHostItem] = provider.getChildren();
    const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
    assert.ok(resourcesGroup);
    const [resourceItem] = provider.getChildren(resourcesGroup);
    const commandsGroup = provider.getChildren(resourceItem).find(item => item.contextValue === 'commandsGroup');
    assert.ok(commandsGroup);

    return provider.getChildren(commandsGroup);
}

function makeTreeProviderWithLaunchService(appHosts: readonly AppHostDisplayInfo[], launchService: AppHostLaunchService): AspireAppHostTreeProvider {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    const repository = {
        viewMode: 'global',
        appHosts,
        workspaceResources: [],
        workspaceAppHostPath: undefined,
        workspaceAppHostCandidatePaths: [],
        workspaceAppHostName: undefined,
        workspaceAppHostDescription: undefined,
        onDidChangeData,
    } as unknown as AppHostDataRepository;

    return new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
}

function makeWorkspaceTreeProvider(workspaceAppHostDescription: string): AspireAppHostTreeProvider {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    const repository = {
        viewMode: 'workspace',
        appHosts: [],
        workspaceResources: [makeResource()],
        workspaceAppHostPath: '/workspace/apps/Store/AppHost.csproj',
        workspaceAppHostCandidatePaths: ['/workspace/apps/Store/AppHost.csproj'],
        workspaceAppHostName: 'AppHost.csproj',
        workspaceAppHostDescription,
        onDidChangeData,
    } as unknown as AppHostDataRepository;

    return new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());
}

function registerTreeCommandCallbacks(
    sandbox: sinon.SinonSandbox,
    provider: AspireAppHostTreeProvider,
    repository: AppHostDataRepository,
): Map<string, (...args: unknown[]) => Promise<unknown>> {
    const callbacks = new Map<string, (...args: unknown[]) => Promise<unknown>>();
    sandbox.stub(vscode.commands, 'registerCommand').callsFake((command, callback) => {
        callbacks.set(command, callback as (...args: unknown[]) => Promise<unknown>);
        return { dispose: () => { } };
    });
    registerTreeViewCommands(provider, repository);

    return callbacks;
}

interface ShellProof {
    readonly directory: string;
    readonly cliPath: string;
    readonly appHostMarkerPath: string;
    readonly resourceMarkerPath: string;
    run(commandLine: string, expectedArgs: readonly string[]): void;
    runPowerShell(commandLine: string, expectedArgs: readonly string[], shellPath: string): void;
    dispose(): void;
}

function createShellProof(): ShellProof {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-tree-rce-proof-'));
    const cliPath = path.join(directory, 'aspire');
    const argvPath = path.join(directory, 'argv.txt');
    const appHostMarkerPath = path.join(directory, 'apphost-pwned');
    const resourceMarkerPath = path.join(directory, 'resource-pwned');

    fs.writeFileSync(cliPath, '#!/bin/sh\nprintf "%s\\n" "$@" > "$PROOF_ARGV"\n');
    fs.chmodSync(cliPath, 0o700);

    return {
        directory,
        cliPath,
        appHostMarkerPath,
        resourceMarkerPath,
        run(commandLine: string, expectedArgs: readonly string[]): void {
            childProcess.execFileSync('/bin/sh', ['-c', commandLine], {
                env: { ...process.env, PROOF_ARGV: argvPath },
                stdio: 'ignore',
            });

            const actualArgs = fs.readFileSync(argvPath, 'utf8').trimEnd().split('\n');
            assert.deepStrictEqual(actualArgs, expectedArgs);
            assert.strictEqual(fs.existsSync(appHostMarkerPath), false, 'AppHost path payload should not execute');
            assert.strictEqual(fs.existsSync(resourceMarkerPath), false, 'resource payload should not execute');
        },
        runPowerShell(commandLine: string, expectedArgs: readonly string[], shellPath: string): void {
            childProcess.execFileSync(shellPath, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', commandLine], {
                env: { ...process.env, PROOF_ARGV: argvPath },
                stdio: 'ignore',
            });

            const actualArgs = fs.readFileSync(argvPath, 'utf8').trimEnd().split('\n');
            assert.deepStrictEqual(actualArgs, expectedArgs);
            assert.strictEqual(fs.existsSync(appHostMarkerPath), false, 'AppHost path payload should not execute');
            assert.strictEqual(fs.existsSync(resourceMarkerPath), false, 'resource payload should not execute');
        },
        dispose(): void {
            removeDirectorySafely(directory);
        },
    };
}

function getPowerShellForShellProof(): string | undefined {
    if (process.platform === 'win32') {
        // The proof CLI is a shebang script so PowerShell invokes it as a native
        // command on Unix hosts. Windows coverage would need a compiled shim to
        // exercise the same native-executable boundary.
        return undefined;
    }

    for (const candidate of ['pwsh', 'pwsh.exe']) {
        const result = childProcess.spawnSync(candidate, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], {
            stdio: 'ignore',
        });
        if (result.status === 0 && result.error === undefined) {
            return candidate;
        }
    }

    return undefined;
}

function makeProofTerminalProvider(sandbox: sinon.SinonSandbox, proof: ShellProof, commandLines: string[]): { terminalProvider: AspireTerminalProvider; dispose: () => void } {
    const subscriptions: vscode.Disposable[] = [];
    const terminalProvider = new AspireTerminalProvider(subscriptions);
    terminalProvider.rpcServerConnectionInfo = {
        address: 'http://localhost:1234',
        token: 'rpc-token',
        cert: 'rpc-cert',
    };
    terminalProvider.dcpServerConnectionInfo = {
        address: 'http://localhost:5678',
        token: 'dcp-token',
        certificate: 'dcp-cert',
    };
    sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: proof.cliPath, available: true, source: 'configured' });
    sandbox.stub(terminalProvider, 'isCliDebugLoggingEnabled').returns(false);
    const commandSubscription = terminalProvider.onDidSendAspireCommand(event => commandLines.push(event.commandLine));
    const aspireTerminal = {
        terminal: {
            shellIntegration: {
                executeCommand: (_commandLine: string) => {
                    return {} as vscode.TerminalShellExecution;
                }
            },
            sendText: () => assert.fail('expected shell integration to execute the command'),
            show: () => { }
        } as unknown as vscode.Terminal,
        dispose: () => { },
    };
    sandbox.stub(terminalProvider, 'getAspireTerminal').returns(aspireTerminal);
    sandbox.stub(terminalProvider as unknown as { createAspireEditorTerminal: () => typeof aspireTerminal }, 'createAspireEditorTerminal').returns(aspireTerminal);

    return {
        terminalProvider,
        dispose: () => {
            commandSubscription.dispose();
            terminalProvider.dispose();
            subscriptions.forEach(subscription => subscription.dispose());
        },
    };
}

async function flushPromises(): Promise<void> {
    await new Promise(resolve => setImmediate(resolve));
}

async function waitForCondition(condition: () => boolean, message: string): Promise<void> {
    for (let i = 0; i < 100; i++) {
        if (condition()) {
            return;
        }

        await flushPromises();
    }

    assert.fail(message);
}

suite('shortenPath', () => {
    test('.csproj returns just the filename', () => {
        assert.strictEqual(shortenPath('/home/user/repos/MyApp/MyApp.AppHost.csproj'), 'MyApp.AppHost.csproj');
    });

    test('.csproj with backslashes returns just the filename', () => {
        assert.strictEqual(shortenPath('C:\\Users\\dev\\MyApp\\MyApp.AppHost.csproj'), 'MyApp.AppHost.csproj');
    });

    test('non-csproj returns parent/filename', () => {
        assert.strictEqual(shortenPath('/home/user/repos/MyApp/AppHost.cs'), 'MyApp/AppHost.cs');
    });

    test('non-csproj with backslashes returns parent/filename', () => {
        assert.strictEqual(shortenPath('C:\\Users\\dev\\MyApp\\AppHost.cs'), 'MyApp/AppHost.cs');
    });

    test('single segment returns as-is', () => {
        assert.strictEqual(shortenPath('AppHost.cs'), 'AppHost.cs');
    });

    test('two segments returns parent/filename', () => {
        assert.strictEqual(shortenPath('MyApp/AppHost.cs'), 'MyApp/AppHost.cs');
    });
});

suite('shortenPaths', () => {
    test('unique project filenames return just the filename', () => {
        const paths = [
            '/home/user/folder1/App1.AppHost.csproj',
            '/home/user/folder2/App2.AppHost.fsproj',
            '/home/user/folder3/App3.AppHost.vbproj',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'App1.AppHost.csproj',
            'App2.AppHost.fsproj',
            'App3.AppHost.vbproj',
        ]);
    });

    test('duplicate filenames add parent directory to disambiguate', () => {
        const paths = [
            '/home/user/folder1/Project.csproj',
            '/home/user/folder2/Project.csproj',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'folder1/Project.csproj',
            'folder2/Project.csproj',
        ]);
    });

    test('duplicate filenames with same parent add more segments', () => {
        const paths = [
            '/home/a/shared/Project.csproj',
            '/home/b/shared/Project.csproj',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'a/shared/Project.csproj',
            'b/shared/Project.csproj',
        ]);
    });

    test('single non-project file returns parent and filename', () => {
        const paths = ['/home/user/repos/MyApp/AppHost.cs'];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'MyApp/AppHost.cs',
        ]);
    });

    test('mixed project and non-project files use project-aware minimum depth', () => {
        const paths = [
            '/home/user/App1/App1.AppHost.csproj',
            '/home/user/App2/AppHost.cs',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'App1.AppHost.csproj',
            'App2/AppHost.cs',
        ]);
    });

    test('duplicate filenames exhaust segments and return full path', () => {
        const paths = [
            'C:\\folder\\Project.csproj',
            'D:\\folder\\Project.csproj',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, paths);
    });

    test('duplicate paths return the same shortened label for each occurrence', () => {
        const paths = [
            '/home/user/folder1/Project.csproj',
            '/home/user/folder1/Project.csproj',
        ];

        const result = shortenPaths(paths);

        assert.deepStrictEqual(result, [
            'Project.csproj',
            'Project.csproj',
        ]);
    });

    test('case-distinct Windows paths keep distinct labels', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            const result = shortenPaths([
                '/workspace/AppHost/apphost.mts',
                '/workspace/apphost/apphost.mts',
            ]);

            assert.deepStrictEqual(result, [
                'AppHost/apphost.mts',
                'apphost/apphost.mts',
            ]);
        } finally {
            platformStub.restore();
        }
    });
});

suite('AspireAppHostTreeProvider', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('global apphost labels add enough parent folders to disambiguate duplicate filenames', () => {
        const appHosts = [
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
            }),
        ];
        const provider = makeTreeProvider(appHosts);

        const labels = provider.getChildren().map(item => item.label);

        assert.deepStrictEqual(labels, [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
    });

    test('global apphost labels keep single-path shortening behavior', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.cs',
                appHostPid: 1,
            }),
        ]);

        const [item] = provider.getChildren();

        assert.strictEqual(item.label, 'Store/AppHost.cs');
    });

    test('global AppHost shows stopping state immediately after stop command', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [makeAppHost({ appHostPath })],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const launchService = makeLaunchService();
        const stopStub = sandbox.stub(launchService, 'stopAppHost').resolves({ outcome: 'stopped', controller: 'external' });
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'appHost:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual((stoppingItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.strictEqual(stopStub.calledOnce, true);
        assert.strictEqual(stopStub.firstCall.args[0], appHostPath);
        provider.dispose();
    });

    test('global stop notification requests refresh and marks stopping for running apphosts', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [makeAppHost({ appHostPath })],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        provider.notifyAppHostStopping(appHostPath);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'appHost:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        provider.dispose();
    });

    test('global stop notification can refresh without marking a surviving apphost as stopping', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [makeAppHost({ appHostPath })],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        provider.notifyAppHostStopping(appHostPath, false);

        const [appHostItem] = provider.getChildren();
        assert.strictEqual(appHostItem.contextValue, 'appHost');
        assert.notStrictEqual(appHostItem.description, 'Stopping...');
        assert.strictEqual(provider.stoppingPaths.length, 0);
        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        provider.dispose();
    });

    test('global stop state preserves case-distinct Windows AppHosts', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.basename(path.dirname(String(filePath))) === 'AppHost' ? 100n : 101n,
        }) as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/apphost.mts';
        const lowerCasePath = '/workspace/apphost/apphost.mts';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: upperCasePath, appHostPid: 100 }),
                makeAppHost({ appHostPath: lowerCasePath, appHostPid: 101 }),
            ],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh: sandbox.stub(),
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        try {
            provider.notifyAppHostStopping(upperCasePath);

            const items = provider.getChildren();
            assert.strictEqual(items[0].contextValue, 'appHost:stopping');
            assert.strictEqual(items[1].contextValue, 'appHost');
        } finally {
            provider.dispose();
            statStub.restore();
            platformStub.restore();
        }
    });

    test('stop AppHost requests refresh after the shared lifecycle operation completes', async () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        let resolveStop: (() => void) | undefined;
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [makeAppHost({ appHostPath })],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const launchService = makeLaunchService();
        sandbox.stub(launchService, 'stopAppHost').returns(new Promise(resolve => {
            resolveStop = () => resolve({ outcome: 'stopped', controller: 'external' });
        }));
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
        const [item] = provider.getChildren();

        const stopTask = provider.stopAppHost(item as any);

        assert.strictEqual(requestAppHostStopRefresh.callCount, 0);
        resolveStop?.();
        await stopTask;

        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        provider.dispose();
    });

    test('stop AppHost clears optimistic state when the shared lifecycle operation does not stop', async () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const results = [
            { outcome: 'notRunning' as const, controller: 'none' as const },
            { outcome: 'alreadyStarting' as const, controller: 'editor' as const },
            { outcome: 'ambiguousSession' as const, controller: 'editor' as const },
        ];

        for (const result of results) {
            const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
            const requestAppHostStopRefresh = sandbox.stub();
            const repository = {
                viewMode: 'global' as ViewMode,
                appHosts: [makeAppHost({ appHostPath })],
                workspaceResources: [],
                workspaceAppHostPath: undefined,
                workspaceAppHostCandidatePaths: [],
                workspaceAppHostName: undefined,
                workspaceAppHostDescription: undefined,
                requestAppHostStopRefresh,
                onDidChangeData,
            } as unknown as AppHostDataRepository;
            const launchService = makeLaunchService();
            sandbox.stub(launchService, 'stopAppHost').resolves(result);
            const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
            const [item] = provider.getChildren();

            await provider.stopAppHost(item as any);

            const [unchangedItem] = provider.getChildren();
            assert.strictEqual(unchangedItem.contextValue, 'appHost');
            assert.notStrictEqual(unchangedItem.description, 'Stopping...');
            assert.strictEqual(requestAppHostStopRefresh.callCount, 0);
            provider.dispose();
        }
    });

    test('workspace stop notification requests refresh and marks stopping for running apphosts', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        provider.notifyAppHostStopping(appHostPath);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        provider.dispose();
    });

    test('workspace stop notification marks stopping when debug session reports workspace folder path', () => {
        const workspaceRoot = path.resolve('workspace');
        const appHostPath = path.join(workspaceRoot, 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        provider.notifyAppHostStopping(workspaceRoot);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [workspaceRoot]);
        assert.deepStrictEqual(provider.stoppingPaths, [appHostPath]);
        provider.dispose();
    });

    test('stop notification does not mark non-running apphosts as stopping', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const unknownAppHostPath = path.resolve('workspace', 'apps', 'Billing', 'AppHost.csproj');
        const requestAppHostStopRefresh = sandbox.stub();
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: undefined,
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            requestAppHostStopRefresh,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        provider.notifyAppHostStopping(appHostPath);
        provider.notifyAppHostStopping(unknownAppHostPath);

        const [candidateItem] = provider.getChildren();
        assert.strictEqual(candidateItem.contextValue, 'workspaceAppHost');
        assert.notStrictEqual(candidateItem.description, 'Stopping...');
        assert.strictEqual(provider.stoppingPaths.length, 0);
        assert.strictEqual(requestAppHostStopRefresh.callCount, 2);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        assert.deepStrictEqual(requestAppHostStopRefresh.secondCall.args, [unknownAppHostPath]);
        provider.dispose();
    });

    test('workspace AppHost shows stopping state immediately after stop command', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const launchService = makeLaunchService();
        const stopStub = sandbox.stub(launchService, 'stopAppHost').resolves({ outcome: 'stopped', controller: 'external' });
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual((stoppingItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.strictEqual(stopStub.calledOnceWith(appHostPath), true);
        provider.dispose();
    });

    test('workspace AppHost candidate shows stopping state immediately after stop command', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const alternateCandidatePath = path.resolve('workspace', 'apps', 'Billing', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [makeAppHost({ appHostPath, resources: [] })],
            workspaceResources: [],
            workspaceAppHost: undefined,
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath, alternateCandidatePath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const launchService = makeLaunchService();
        const stopStub = sandbox.stub(launchService, 'stopAppHost').resolves({ outcome: 'stopped', controller: 'external' });
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);

        const [updatedItem] = provider.getChildren();
        assert.strictEqual(updatedItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(updatedItem.description, 'Stopping...');
        assert.strictEqual((updatedItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.strictEqual(stopStub.calledOnceWith(appHostPath), true);
        provider.dispose();
    });

    test('workspace tree terminal actions pass hostile paths as inert shell arguments', async function () {
        if (process.platform === 'win32') {
            this.skip();
        }

        const proof = createShellProof();
        const commandLines: string[] = [];
        const proofTerminalProvider = makeProofTerminalProvider(sandbox, proof, commandLines);
        const appHostPath = path.join(proof.directory, 'workspace') + `'; touch ${proof.appHostMarkerPath} #/$(whoami)/"bad"/AppHost.csproj`;
        const resourceName = `cache'; touch ${proof.resourceMarkerPath} #`;
        const resource = makeResource({ name: resourceName, displayName: resourceName });
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [resource],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [resource] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, proofTerminalProvider.terminalProvider, makeLaunchService());

        try {
            const [workspaceItem] = provider.getChildren();
            const [resourceItem] = provider.getChildren(workspaceItem);

            await provider.viewResourceLogs(resourceItem as any);
            proof.run(commandLines[0], ['logs', resourceName, '--apphost', appHostPath]);

            await provider.openResourceTerminal(resourceItem as any);
            proof.run(commandLines[1], ['terminal', 'attach', resourceName, '--apphost', appHostPath]);
        }
        finally {
            provider.dispose();
            proofTerminalProvider.dispose();
            proof.dispose();
        }
    });

    test('workspace tree terminal actions pass hostile paths as inert PowerShell arguments', async function () {
        const powerShellPath = getPowerShellForShellProof();
        if (powerShellPath === undefined) {
            this.skip();
        }

        const platformStub = sandbox.stub(process, 'platform').value('win32');
        const proof = createShellProof();
        const commandLines: string[] = [];
        const proofTerminalProvider = makeProofTerminalProvider(sandbox, proof, commandLines);
        const appHostPath = path.join(proof.directory, 'workspace') + `"\u201C; touch ${proof.appHostMarkerPath} #/$(whoami)/\`whoami\`/AppHost.csproj`;
        const resourceName = `cache"\u201C; touch ${proof.resourceMarkerPath} # $(whoami) \`whoami\``;
        const resource = makeResource({ name: resourceName, displayName: resourceName });
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [resource],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [resource] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, proofTerminalProvider.terminalProvider, makeLaunchService());

        try {
            const [workspaceItem] = provider.getChildren();
            const [resourceItem] = provider.getChildren(workspaceItem);

            await provider.viewResourceLogs(resourceItem as any);
            proof.runPowerShell(commandLines[0], ['logs', resourceName, '--apphost', appHostPath], powerShellPath);

            await provider.openResourceTerminal(resourceItem as any);
            proof.runPowerShell(commandLines[1], ['terminal', 'attach', resourceName, '--apphost', appHostPath], powerShellPath);
        }
        finally {
            platformStub.restore();
            provider.dispose();
            proofTerminalProvider.dispose();
            proof.dispose();
        }
    });

    test('workspace tree terminal actions reject control characters before terminal input', async () => {
        const proof = createShellProof();
        const commandLines: string[] = [];
        const proofTerminalProvider = makeProofTerminalProvider(sandbox, proof, commandLines);
        const appHostPath = path.join(proof.directory, 'workspace') + `\x03\ntouch ${proof.appHostMarkerPath}\n#`;
        const resourceName = `cache\x03\ntouch ${proof.resourceMarkerPath}\n#`;
        const resource = makeResource({ name: resourceName, displayName: resourceName });
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [resource],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [resource] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, proofTerminalProvider.terminalProvider, makeLaunchService());

        try {
            const [workspaceItem] = provider.getChildren();
            const [resourceItem] = provider.getChildren(workspaceItem);

            await assert.rejects(() => provider.viewResourceLogs(resourceItem as any), { message: terminalCommandArgumentControlCharacters });
            await assert.rejects(() => provider.openResourceTerminal(resourceItem as any), { message: terminalCommandArgumentControlCharacters });
            // restartResource no longer flows through the terminal: it spawns the CLI directly with
            // shell:false, so control characters in the resource name are passed as an inert argv
            // element rather than rejected. That path is covered by the resource-command tests below.

            assert.deepStrictEqual(commandLines, []);
            assert.strictEqual(fs.existsSync(proof.appHostMarkerPath), false, 'AppHost control-character payload should not execute');
            assert.strictEqual(fs.existsSync(proof.resourceMarkerPath), false, 'resource control-character payload should not execute');
        }
        finally {
            provider.dispose();
            proofTerminalProvider.dispose();
            proof.dispose();
        }
    });

    test('stopping state clears when AppHost leaves the running list', () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const changeEmitter = new vscode.EventEmitter<void>();
        let appHosts = [makeAppHost({ appHostPath })];
        const repository = {
            viewMode: 'global' as ViewMode,
            get appHosts() {
                return appHosts;
            },
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            onDidChangeData: changeEmitter.event,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);
        appHosts = [];
        changeEmitter.fire();
        appHosts = [makeAppHost({ appHostPath })];

        const [reappearedItem] = provider.getChildren();
        assert.strictEqual(reappearedItem.contextValue, 'appHost');
        provider.dispose();
        changeEmitter.dispose();
    });

    test('workspace AppHost tooltip explains aspire ls selection metadata', () => {
        const provider = makeWorkspaceTreeProvider('Workspace view selected because aspire ls found one buildable C# AppHost.');

        const [item] = provider.getChildren();

        assert.strictEqual(item.tooltip, 'Workspace view selected because aspire ls found one buildable C# AppHost.');
    });

    test('global AppHost tooltip explains aspire ls selection metadata', () => {
        const provider = makeTreeProvider([makeAppHost({ appHostPath: '/workspace/AppHost.csproj' })], 'global', 'Global view selected because aspire ls found 2 buildable AppHosts.');

        const [item] = provider.getChildren();

        assert.strictEqual(item.tooltip, 'Global view selected because aspire ls found 2 buildable AppHosts.\n/workspace/AppHost.csproj');
    });

    test('runAppHost rethrows launch failures after showing the error', async () => {
        const launchError = new Error('launch failed');
        const launchService = {
            launch: sandbox.stub().rejects(launchError),
            isLaunching: () => false,
            launchingPaths: [],
            onDidChangeLaunchingState: () => ({ dispose: () => { } }),
            onDidChangeOperationState: () => ({ dispose: () => { } }),
            getActiveOperation: () => undefined,
        } as unknown as AppHostLaunchService;
        const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = makeTreeProviderWithLaunchService([
            makeAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj', appHostPid: 1 }),
        ], launchService);

        await assert.rejects(provider.runAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj' } as any, true), /launch failed/);

        assert.strictEqual(showErrorStub.callCount, 1);
    });

    test('runAppHost rethrows cancellations without showing the error', async () => {
        const launchService = {
            launch: sandbox.stub().rejects(new vscode.CancellationError()),
            isLaunching: () => false,
            launchingPaths: [],
            onDidChangeLaunchingState: () => ({ dispose: () => { } }),
            onDidChangeOperationState: () => ({ dispose: () => { } }),
            getActiveOperation: () => undefined,
        } as unknown as AppHostLaunchService;
        const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = makeTreeProviderWithLaunchService([
            makeAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj', appHostPid: 1 }),
        ], launchService);

        await assert.rejects(provider.runAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj' } as any, true), vscode.CancellationError);

        assert.strictEqual(showErrorStub.callCount, 0);
    });

    test('dashboard quick pick labels add enough parent folders to disambiguate duplicate filenames', async () => {
        const appHosts = [
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:1001',
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'http://localhost:1002',
            }),
        ];
        const provider = makeTreeProvider(appHosts);
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (items as readonly vscode.QuickPickItem[])[0]);
        const openExternalStub = sandbox.stub(vscode.env, 'openExternal').resolves(true);

        await provider.openDashboard();

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
        assert.strictEqual(openExternalStub.callCount, 1);
    });

    test('workspace dashboard command falls back to running AppHost dashboards', async () => {
        const appHosts = [
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:1001',
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'http://localhost:1002',
            }),
        ];
        const provider = makeTreeProvider(appHosts, 'workspace');
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (items as readonly vscode.QuickPickItem[])[1]);
        const openExternalStub = sandbox.stub(vscode.env, 'openExternal').resolves(true);

        await provider.openDashboard();

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
        assert.strictEqual(openExternalStub.callCount, 1);
        assert.strictEqual(openExternalStub.getCall(0).args[0].toString(), 'http://localhost:1002/');
    });

    test('workspace view shows multiple running AppHosts before workspace discovery completes', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
            }),
        ], 'workspace');

        const items = provider.getChildren();

        assert.deepStrictEqual(items.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
    });

    test('openDashboard stays silent when dashboard selection is canceled', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:1001',
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'http://localhost:1002',
            }),
        ]);
        sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const openExternalStub = sandbox.stub(vscode.env, 'openExternal').resolves(true);

        await provider.openDashboard();

        assert.strictEqual(showInformationMessageStub.callCount, 0);
        assert.strictEqual(openExternalStub.callCount, 0);
    });

    test('openDashboard does not fall back to another AppHost for an explicit AppHost item', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/workspace/apps/Store/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: null,
            }),
            makeAppHost({
                appHostPath: '/workspace/samples/Store/AppHost.csproj',
                appHostPid: 2,
                dashboardUrl: 'http://localhost:1002',
            }),
        ]);
        const [appHostItem] = provider.getChildren();
        const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        const openExternalStub = sandbox.stub(vscode.env, 'openExternal').resolves(true);

        await provider.openDashboard(appHostItem);

        assert.strictEqual(showInformationMessageStub.callCount, 1);
        assert.strictEqual(showQuickPickStub.callCount, 0);
        assert.strictEqual(openExternalStub.callCount, 0);
    });

    test('openDashboardToSide opens the dashboard in the integrated browser side group', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                dashboardUrl: 'http://localhost:1001',
            }),
        ]);
        sandbox.stub(vscode.commands, 'getCommands').resolves(['workbench.action.browser.open']);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const openExternalStub = sandbox.stub(vscode.env, 'openExternal').resolves(true);

        await provider.openDashboardToSide();

        assert.strictEqual(openExternalStub.callCount, 0);
        assert.strictEqual(executeCommandStub.callCount, 1);
        assert.strictEqual(executeCommandStub.getCall(0).args[0], 'workbench.action.browser.open');
        assert.deepStrictEqual(executeCommandStub.getCall(0).args[1], {
            url: 'http://localhost:1001',
            openToSide: true,
        });
    });

    test('openDashboardToSide falls back to simple browser API on VS Code 1.98', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                dashboardUrl: 'http://localhost:1001',
            }),
        ]);
        sandbox.stub(vscode.commands, 'getCommands').resolves([]);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);

        await provider.openDashboardToSide();

        assert.strictEqual(executeCommandStub.callCount, 1);
        assert.strictEqual(executeCommandStub.getCall(0).args[0], 'simpleBrowser.api.open');
        const uri = executeCommandStub.getCall(0).args[1] as vscode.Uri;
        assert.strictEqual(uri.scheme, 'http');
        assert.strictEqual(uri.authority, 'localhost:1001');
        assert.deepStrictEqual(executeCommandStub.getCall(0).args[2], {
            viewColumn: vscode.ViewColumn.Beside,
            preserveFocus: false,
        });
    });

    test('openDashboardToSide warns when there is no dashboard URL to open', async () => {
        const provider = makeTreeProvider([]);
        const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);

        await provider.openDashboardToSide();

        assert.strictEqual(showInformationMessageStub.callCount, 1);
        assert.strictEqual(executeCommandStub.callCount, 0);
    });

    test('openDashboardToSide rejects non-web dashboard URLs', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                dashboardUrl: 'vscode://malicious-command',
            }),
        ]);
        const showWarningMessageStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);

        await provider.openDashboardToSide();

        assert.strictEqual(showWarningMessageStub.callCount, 1);
        assert.strictEqual(executeCommandStub.callCount, 0);
    });

    test('non-http endpoints remain visible but are not clickable', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        urls: [
                            { name: 'http', displayName: 'HTTP', url: 'http://localhost:5000', isInternal: false },
                            { name: 'tcp', displayName: 'TCP', url: 'tcp://localhost:1433', isInternal: false },
                            { name: 'internal', displayName: 'Internal', url: 'http://127.0.0.1:1', isInternal: true },
                        ],
                    }),
                ],
            }),
        ]);

        const [appHost] = provider.getChildren();
        const [resourcesGroup] = provider.getChildren(appHost);
        const [resource] = provider.getChildren(resourcesGroup);
        const endpoints = provider.getChildren(resource) as readonly vscode.TreeItem[];

        assert.strictEqual(endpoints.length, 2);
        assert.strictEqual(endpoints[0].contextValue, 'endpointUrl');
        assert.strictEqual(endpoints[0].command?.command, 'vscode.open');
        assert.strictEqual(endpoints[1].contextValue, 'endpointUrlNonHttp');
        assert.strictEqual(endpoints[1].command, undefined);
    });

    test('enabled command tree item uses context menu execution only', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            restart: { displayName: 'Restart', description: 'Restart the resource', state: 'Enabled' },
                        },
                    }),
                ],
            }),
        ]);

        const [commandItem] = getResourceCommandItems(provider);

        assert.strictEqual(commandItem.label, 'Restart');
        assert.strictEqual(commandItem.contextValue, 'resourceCommand:enabled');
        assert.strictEqual((commandItem.iconPath as vscode.ThemeIcon).id, 'debug-restart');
        assert.strictEqual(commandItem.command, undefined);
    });

    test('legacy command without state is treated as enabled', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            restart: { displayName: 'Restart', description: 'Restart the resource' },
                        },
                    }),
                ],
            }),
        ]);

        const [commandItem] = getResourceCommandItems(provider);

        assert.strictEqual(commandItem.label, 'Restart');
        assert.strictEqual(commandItem.contextValue, 'resourceCommand:enabled');
        assert.strictEqual((commandItem.iconPath as vscode.ThemeIcon).id, 'debug-restart');
        assert.strictEqual(commandItem.command, undefined);
    });

    test('legacy command without state is shown in execute quick pick', async () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            restart: { displayName: 'Restart', description: 'Restart the resource' },
                        },
                    }),
                ],
            }),
        ]);
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup);
        const [resourceItem] = provider.getChildren(resourcesGroup);

        await assert.rejects(provider.executeResourceCommand(resourceItem as any), /Canceled/);

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => item.label), ['restart']);
    });

    test('legacy command item without state executes from context menu', async () => {
        const sentCommands: AspireSubcommand[] = [];
        const runResourceCommandCalls: Array<[string, string | undefined, string, readonly string[]]> = [];
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => sentCommands.push(command),
        } as unknown as AspireTerminalProvider;
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [
                makeAppHost({
                    resources: [
                        makeResource({
                            commands: {
                                '$(legacy)': { displayName: 'Legacy command', description: null },
                            },
                        }),
                    ],
                }),
            ],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            onDidChangeData,
            runResourceCommand: async (resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = []) => {
                runResourceCommandCalls.push([resourceName, appHostPath, commandName, additionalArgs]);
                return { stdout: '', stderr: '' };
            },
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const infoStub = sandbox.stub(vscode.window, 'showInformationMessage');
        const [commandItem] = getResourceCommandItems(provider);

        await provider.executeResourceCommandItem(commandItem as any);

        // The command runs over the hidden CLI backchannel, not the visible terminal, and reports
        // success inside VS Code.
        assert.deepStrictEqual(sentCommands, []);
        assert.deepStrictEqual(runResourceCommandCalls, [['my-service', '/test/AppHost.csproj', '$(legacy)', []]]);
        assert.strictEqual(infoStub.calledOnce, true);
    });

    test('resource command item returns failed execution outcome after reporting error', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: () => { throw new Error('terminal should not be used'); },
        } as unknown as AspireTerminalProvider;
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [
                makeAppHost({
                    resources: [
                        makeResource({
                            commands: {
                                fail: { displayName: 'Fail command', description: null },
                            },
                        }),
                    ],
                }),
            ],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            onDidChangeData,
            runResourceCommand: async () => {
                throw new Error('resource command failed');
            },
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const errorStub = sandbox.stub(vscode.window, 'showErrorMessage');
        const [commandItem] = getResourceCommandItems(provider);

        const outcome = await provider.executeResourceCommandItem(commandItem as any);

        assert.deepStrictEqual(outcome, { success: false, hadOutput: false });
        assert.strictEqual(errorStub.calledOnce, true);
    });

    test('lifecycle resource command failures reach telemetry', async () => {
        sandbox.stub(vscode.window, 'withProgress').callsFake((_options: any, task: any) => task({ report: () => { } }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => { } }) }));
        sandbox.stub(vscode.window, 'showErrorMessage');

        const invocations: Array<{ command: string; outcome: string; errorKind?: string }> = [];
        const invocationSubscription = onDidInvokeCommand(event => invocations.push(event));
        const runResourceCommandCalls: Array<[string, string | undefined, string, readonly string[]]> = [];
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [
                makeAppHost({
                    resources: [
                        makeResource({
                            commands: {
                                start: { displayName: 'Start', description: null },
                                stop: { displayName: 'Stop', description: null },
                                restart: { displayName: 'Restart', description: null },
                            },
                        }),
                    ],
                }),
            ],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            onDidChangeData: (() => ({ dispose: () => { } })) as vscode.Event<void>,
            runResourceCommand: async (resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = []) => {
                runResourceCommandCalls.push([resourceName, appHostPath, commandName, additionalArgs]);
                throw new Error(`${commandName} failed`);
            },
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        try {
            const [appHostItem] = provider.getChildren();
            const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
            assert.ok(resourcesGroup);
            const [resourceItem] = provider.getChildren(resourcesGroup);

            const outcomes = [
                await withCommandTelemetry('aspire-vscode.stopResource', () => provider.stopResource(resourceItem as any), { source: 'tree' }),
                await withCommandTelemetry('aspire-vscode.startResource', () => provider.startResource(resourceItem as any), { source: 'tree' }),
                await withCommandTelemetry('aspire-vscode.restartResource', () => provider.restartResource(resourceItem as any), { source: 'tree' }),
            ];

            assert.deepStrictEqual(outcomes, [
                { success: false, hadOutput: false },
                { success: false, hadOutput: false },
                { success: false, hadOutput: false },
            ]);
            assert.deepStrictEqual(runResourceCommandCalls, [
                ['my-service', '/test/AppHost.csproj', 'stop', []],
                ['my-service', '/test/AppHost.csproj', 'start', []],
                ['my-service', '/test/AppHost.csproj', 'restart', []],
            ]);
            assert.deepStrictEqual(invocations.map(event => [event.command, event.outcome, event.errorKind]), [
                ['aspire-vscode.stopResource', 'error', 'HandledError'],
                ['aspire-vscode.startResource', 'error', 'HandledError'],
                ['aspire-vscode.restartResource', 'error', 'HandledError'],
            ]);
        }
        finally {
            invocationSubscription.dispose();
            provider.dispose();
        }
    });

    test('resource with commands is expandable even without URLs, health checks, or child resources', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            restart: { displayName: 'Restart', description: 'Restart the resource', state: 'Enabled' },
                        },
                    }),
                ],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup);
        const [resourceItem] = provider.getChildren(resourcesGroup);

        assert.strictEqual(resourceItem.collapsibleState, vscode.TreeItemCollapsibleState.Collapsed);
    });

    test('disabled command tree item is not executable', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            start: { displayName: 'Start', description: 'Start the resource', state: 'Disabled' },
                        },
                    }),
                ],
            }),
        ]);

        const [commandItem] = getResourceCommandItems(provider);

        assert.strictEqual(commandItem.label, 'Start');
        assert.strictEqual(commandItem.contextValue, 'resourceCommand:disabled');
        assert.strictEqual(commandItem.description, '(disabled)');
        assert.strictEqual((commandItem.iconPath as vscode.ThemeIcon).id, 'play');
        assert.strictEqual(commandItem.command, undefined);
    });

    test('hidden command state is not shown', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            save: { displayName: 'Save', description: 'Save the resource', state: 'Hidden' },
                        },
                    }),
                ],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup);
        const [resourceItem] = provider.getChildren(resourcesGroup);

        assert.strictEqual(resourceItem.collapsibleState, vscode.TreeItemCollapsibleState.None);
        assert.strictEqual(provider.getChildren(resourceItem).length, 0);
    });

    test('api-only command is not shown', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {
                            run: { displayName: 'Run', description: 'Run headless operation', state: 'Enabled', visibility: 'Api' },
                        },
                    }),
                ],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup);
        const [resourceItem] = provider.getChildren(resourcesGroup);

        assert.strictEqual(resourceItem.collapsibleState, vscode.TreeItemCollapsibleState.None);
        assert.strictEqual(provider.getChildren(resourceItem).length, 0);
    });

    test('empty command map does not show commands group', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [
                    makeResource({
                        commands: {},
                    }),
                ],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(item => item.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup);
        const [resourceItem] = provider.getChildren(resourcesGroup);

        assert.strictEqual(resourceItem.collapsibleState, vscode.TreeItemCollapsibleState.None);
        assert.strictEqual(provider.getChildren(resourceItem).length, 0);
    });
});

suite('AppHostDataRepository', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
        // Keep the capability probe out of the shared spawn fake while exercising streamed discovery.
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            capabilities: [lsJsonStreamCapability],
        } as any);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('workspace apphost name uses all candidates to disambiguate duplicate filenames', async () => {
        const clock = sinon.useFakeTimers();
        let clockRestored = false;
        let emitCandidates: ((candidates: CandidateAppHostDisplayInfo[]) => void) | undefined;
        let completeDiscovery: (() => void) | undefined;
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] !== 'ls') {
                return { kill: () => { } } as any;
            }

            emitCandidates = candidates => {
                for (const candidate of candidates) {
                    options?.lineCallback?.(JSON.stringify(candidate));
                }
            };
            completeDiscovery = () => options?.exitCallback?.(0);
            return { kill: () => { } } as any;
        });
        const repository = new AppHostDataRepository(makeTerminalProvider());

        try {
            await clock.tickAsync(0);
            assert.ok(emitCandidates);
            assert.ok(completeDiscovery);

            emitCandidates([
                {
                    path: '/workspace/apps/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                    selected: true,
                },
                {
                    path: '/workspace/samples/Store/AppHost.csproj',
                    language: 'csharp',
                    status: 'buildable',
                    selected: false,
                },
            ]);
            await clock.tickAsync(50);

            assert.deepStrictEqual(repository.workspaceAppHostCandidatePaths, [
                '/workspace/apps/Store/AppHost.csproj',
                '/workspace/samples/Store/AppHost.csproj',
            ]);
            assert.strictEqual(repository.workspaceAppHostName, undefined);

            clock.restore();
            clockRestored = true;
            completeDiscovery();
            await waitForCondition(
                () => repository.workspaceAppHostName === 'apps/Store/AppHost.csproj',
                'workspace AppHost name was not updated');

            assert.strictEqual(repository.workspaceAppHostName, 'apps/Store/AppHost.csproj');
        } finally {
            if (!clockRestored) {
                clock.restore();
            }
            completeDiscovery?.();
            repository.dispose();
        }
    });
});

suite('resolveAppHostSourcePath', () => {
    test('returns source files unchanged', () => {
        const appHostTsPath = buildPath(path.sep, 'repo', 'MyApp', 'apphost.ts');
        const appHostCsPath = buildPath(path.sep, 'repo', 'MyApp', 'AppHost.cs');

        assert.strictEqual(resolveAppHostSourcePath(appHostTsPath), appHostTsPath);
        assert.strictEqual(resolveAppHostSourcePath(appHostCsPath), appHostCsPath);
    });

    test('prefers AppHost.cs for csproj paths', () => {
        const csprojPath = buildPath(path.sep, 'repo', 'MyApp', 'MyApp.AppHost.csproj');
        const appHostCsPath = buildPath(path.sep, 'repo', 'MyApp', 'AppHost.cs');

        const result = resolveAppHostSourcePath(csprojPath, candidate => candidate === appHostCsPath);
        assert.strictEqual(result, appHostCsPath);
    });

    test('prefers lowercase apphost.cs for file-based csproj paths', () => {
        const csprojPath = buildPath(path.sep, 'repo', 'MyApp', 'MyApp.AppHost.csproj');
        const fileBasedAppHostPath = buildPath(path.sep, 'repo', 'MyApp', 'apphost.cs');

        const result = resolveAppHostSourcePath(csprojPath, candidate => candidate === fileBasedAppHostPath);
        assert.strictEqual(result, fileBasedAppHostPath);
    });

    test('falls back to Program.cs for csproj paths', () => {
        const csprojPath = buildPath(path.sep, 'repo', 'MyApp', 'MyApp.AppHost.csproj');
        const programCsPath = buildPath(path.sep, 'repo', 'MyApp', 'Program.cs');

        const result = resolveAppHostSourcePath(csprojPath, candidate => candidate === programCsPath);
        assert.strictEqual(result, programCsPath);
    });

    test('falls back to csproj when no source file is present', () => {
        const csprojPath = buildPath(path.sep, 'repo', 'MyApp', 'MyApp.AppHost.csproj');

        const result = resolveAppHostSourcePath(csprojPath, () => false);
        assert.strictEqual(result, csprojPath);
    });
});

suite('getResourceContextValue', () => {
    test('resource with no commands returns just "resource"', () => {
        assert.strictEqual(getResourceContextValue(makeResource()), 'resource');
    });

    test('resource with start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'start': { displayName: null, description: null, state: 'Enabled' } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with resource-start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'resource-start': { displayName: null, description: null, state: 'Enabled' } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with stop command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'stop': { displayName: null, description: null, state: 'Enabled' } },
        }));
        assert.strictEqual(result, 'resource:canStop');
    });

    test('resource with all lifecycle commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'start': { displayName: null, description: null, state: 'Enabled' },
                'stop': { displayName: null, description: null, state: 'Enabled' },
                'restart': { displayName: null, description: null, state: 'Enabled' },
            },
        }));
        assert.strictEqual(result, 'resource:canStart:canStop:canRestart');
    });

    test('resource with legacy lifecycle command has lifecycle context', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'restart': { displayName: null, description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canRestart');
    });

    test('resource with non-lifecycle commands has base context only', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'custom-action': { displayName: null, description: 'do something' } },
        }));
        assert.strictEqual(result, 'resource');
    });

    test('resource with mixed lifecycle and custom commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'restart': { displayName: null, description: null, state: 'Enabled' },
                'custom-action': { displayName: null, description: null, state: 'Enabled' },
            },
        }));
        assert.strictEqual(result, 'resource:canRestart');
    });

    test('resource with terminal enabled property includes terminal context', () => {
        const result = getResourceContextValue(makeResource({
            properties: { 'terminal.enabled': 'true' },
        }));
        assert.strictEqual(result, 'resource:canOpenTerminal');
    });

    test('resource with lifecycle and terminal properties includes both contexts', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'restart': { displayName: null, description: null, state: 'Enabled' },
            },
            properties: { 'terminal.enabled': 'true' },
        }));
        assert.strictEqual(result, 'resource:canRestart:canOpenTerminal');
    });

    test('resource with disabled lifecycle command has base context only', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'start': { displayName: null, description: null, state: 'Disabled' } },
        }));
        assert.strictEqual(result, 'resource');
    });

    test('resource with api-only lifecycle command has base context only', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'start': { displayName: null, description: null, state: 'Enabled', visibility: 'Api' } },
        }));
        assert.strictEqual(result, 'resource');
    });

});

suite('getResourceIcon', () => {
    test('Running + Healthy shows pass icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Healthy }));
        assert.strictEqual(icon.id, 'pass');
    });

    test('Running + Unhealthy shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Unhealthy }));
        assert.strictEqual(icon.id, 'warning');
    });

    test('Running + Degraded shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Degraded }));
        assert.strictEqual(icon.id, 'warning');
    });

    test('Running + error stateStyle shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, stateStyle: StateStyle.Error }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Running + warning stateStyle shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, stateStyle: StateStyle.Warning }));
        assert.strictEqual(icon.id, 'warning');
    });

    test('Active state treated same as Running', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Active }));
        assert.strictEqual(icon.id, 'pass');
    });

    test('Exited with error stateStyle shows error', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Exited, stateStyle: StateStyle.Error }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Exited with non-zero exit code shows error', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Exited, exitCode: 137 }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Finished with exit code 0 shows hollow circle (stopped)', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Finished, exitCode: 0 }));
        assert.strictEqual(icon.id, 'circle-outline');
    });

    test('FailedToStart shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart }));
        assert.strictEqual(icon.id, 'warning');
        assert.ok(icon.color instanceof vscode.ThemeColor);
        assert.strictEqual(icon.color.id, 'list.warningForeground');
    });

    test('FailedToStart with exit code 0 shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart, exitCode: 0 }));
        assert.strictEqual(icon.id, 'warning');
        assert.ok(icon.color instanceof vscode.ThemeColor);
        assert.strictEqual(icon.color.id, 'list.warningForeground');
    });

    test('FailedToStart with exit code -1 shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart, exitCode: -1 }));
        assert.strictEqual(icon.id, 'error');
        assert.ok(icon.color instanceof vscode.ThemeColor);
        assert.strictEqual(icon.color.id, 'list.errorForeground');
    });

    test('FailedToStart with a non-zero exit code shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart, exitCode: 1 }));
        assert.strictEqual(icon.id, 'error');
        assert.ok(icon.color instanceof vscode.ThemeColor);
        assert.strictEqual(icon.color.id, 'list.errorForeground');
    });

    test('RuntimeUnhealthy shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.RuntimeUnhealthy }));
        assert.strictEqual(icon.id, 'warning');
        assert.ok(icon.color instanceof vscode.ThemeColor);
        assert.strictEqual(icon.color.id, 'list.warningForeground');
    });

    test('Starting shows loading spinner', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Starting }));
        assert.strictEqual(icon.id, 'loading~spin');
    });

    test('Building shows loading spinner', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Building }));
        assert.strictEqual(icon.id, 'loading~spin');
    });

    test('Waiting shows loading spinner', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Waiting }));
        assert.strictEqual(icon.id, 'loading~spin');
    });

    test('NotStarted shows record (no spinner)', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.NotStarted }));
        assert.strictEqual(icon.id, 'record');
    });

    test('Finished shows hollow circle (stopped)', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Finished }));
        assert.strictEqual(icon.id, 'circle-outline');
    });

    test('null state shows record', () => {
        const icon = getResourceIcon(makeResource({ state: null }));
        assert.strictEqual(icon.id, 'record');
    });

    test('unknown state shows circle-filled', () => {
        const icon = getResourceIcon(makeResource({ state: 'SomeUnknownState' }));
        assert.strictEqual(icon.id, 'circle-filled');
    });

    test('ValueMissing parameter shows warning icon', () => {
        const icon = getResourceIcon(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.ValueMissing,
        }));

        assert.strictEqual(icon.id, 'warning');
    });
});

suite('getResourceCommandIcon', () => {
    test('start uses play icon', () => {
        assert.strictEqual(getResourceCommandIcon('start', true).id, 'play');
    });

    test('stop uses debug-stop icon', () => {
        assert.strictEqual(getResourceCommandIcon('stop', true).id, 'debug-stop');
    });

    test('restart uses debug-restart icon', () => {
        assert.strictEqual(getResourceCommandIcon('restart', true).id, 'debug-restart');
    });

    test('rebuild uses tools icon', () => {
        assert.strictEqual(getResourceCommandIcon('rebuild', true).id, 'tools');
    });

    test('resource- prefixed lifecycle commands map to the same icons', () => {
        assert.strictEqual(getResourceCommandIcon('resource-restart', true).id, 'debug-restart');
    });

    test('custom command falls back to run icon', () => {
        assert.strictEqual(getResourceCommandIcon('migrate-database', true).id, 'run');
    });

    test('disabled command keeps its icon but is themed disabled', () => {
        const icon = getResourceCommandIcon('stop', false);
        assert.strictEqual(icon.id, 'debug-stop');
        assert.ok(icon.color instanceof vscode.ThemeColor);
    });
});

suite('buildResourceDescription', () => {
    test('no state, health, or exit code returns resource type', () => {
        assert.strictEqual(buildResourceDescription(makeResource()), 'Project');
    });

    test('with state shows type and state', () => {
        assert.strictEqual(buildResourceDescription(makeResource({ state: 'Running' })), 'Project · Running');
    });

    test('with health reports shows count', () => {
        const desc = buildResourceDescription(makeResource({
            healthReports: {
                'check1': { status: 'Healthy', description: null, exceptionMessage: null },
                'check2': { status: 'Unhealthy', description: null, exceptionMessage: null },
            },
        }));
        assert.ok(desc.includes('1/2'));
    });

    test('with exit code shows exit code', () => {
        const desc = buildResourceDescription(makeResource({ exitCode: 137 }));
        assert.ok(desc.includes('137'));
    });

    test('with both health and exit code shows both', () => {
        const desc = buildResourceDescription(makeResource({
            exitCode: 1,
            healthReports: {
                'check1': { status: 'Healthy', description: null, exceptionMessage: null },
            },
        }));
        assert.ok(desc.includes('1/1'));
        assert.ok(desc.includes('Exit Code: 1'));
    });

    test('empty health reports returns resource type', () => {
        assert.strictEqual(buildResourceDescription(makeResource({ healthReports: {} })), 'Project');
    });

    test('parameter with missing value shows humanized state and no stale value', () => {
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.ValueMissing,
            properties: { Value: 'Parameter value has been deleted' },
        }));

        assert.strictEqual(desc, 'Parameter · Value missing');
    });

    test('parameter with non-secret value shows value text', () => {
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: 'The value' },
        }));

        assert.strictEqual(desc, 'Parameter · Running · The value');
    });

    test('parameter with secret value shows masked value', () => {
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: 'super-secret-value' },
            commands: {
                'set-parameter': {
                    displayName: 'Set parameter',
                    description: null,
                    argumentInputs: [
                        {
                            name: 'Value',
                            label: null,
                            description: null,
                            inputType: ResourceCommandInputType.SecretText,
                            placeholder: null,
                            value: null,
                            options: null,
                            maxLength: null,
                        },
                    ],
                },
            },
        }));

        assert.strictEqual(desc, 'Parameter · Running · ●●●●●●●●');
        assert.ok(!desc.includes('super-secret-value'), 'Expected secret value to be masked');
    });

    test('parameter value at display limit is not truncated', () => {
        const value = 'x'.repeat(80);
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: value },
        }));

        assert.strictEqual(desc, `Parameter · Running · ${value}`);
    });

    test('parameter value over display limit is truncated with ellipsis', () => {
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: `${'x'.repeat(80)}y` },
        }));

        assert.strictEqual(desc, `Parameter · Running · ${'x'.repeat(79)}…`);
    });

    test('parameter with empty value does not add a blank value segment', () => {
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: '' },
        }));

        assert.strictEqual(desc, 'Parameter · Running');
    });

    test('secret parameter with redacted (null) value shows masked value', () => {
        // The backchannel redacts sensitive values to null before they reach the extension,
        // so a secret with an actual value arrives as `Value: null`. It must still be masked.
        const desc = buildResourceDescription(makeResource({
            resourceType: 'Parameter',
            state: ResourceState.Running,
            properties: { Value: null },
            commands: {
                'set-parameter': {
                    displayName: 'Set parameter',
                    description: null,
                    argumentInputs: [
                        {
                            name: 'Value',
                            label: null,
                            description: null,
                            inputType: ResourceCommandInputType.SecretText,
                            placeholder: null,
                            value: null,
                            options: null,
                            maxLength: null,
                        },
                    ],
                },
            },
        }));

        assert.strictEqual(desc, 'Parameter · Running · ●●●●●●●●');
    });
});

suite('AspireAppHostTreeProvider.findAppHostElement', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('returns undefined when given empty path', () => {
        const provider = makeTreeProvider([makeAppHost({ appHostPath: '/repo/AppHost/AppHost.csproj' })]);
        assert.strictEqual(provider.findAppHostElement(''), undefined);
        provider.dispose();
    });

    test('returns undefined when no AppHosts are tracked (global mode)', () => {
        const provider = makeTreeProvider([]);
        assert.strictEqual(provider.findAppHostElement('/repo/AppHost/AppHost.csproj'), undefined);
        provider.dispose();
    });

    test('matches an AppHostItem by exact path (global mode)', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const provider = makeTreeProvider([makeAppHost({ appHostPath: hostPath })]);

        const result = provider.findAppHostElement(hostPath);

        assert.ok(result, 'Expected to find an AppHostItem');
        provider.dispose();
    });

    test('matches an AppHostItem by same-directory path (global mode)', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const docPath = '/repo/AppHost/AppHost.cs';
        const provider = makeTreeProvider([makeAppHost({ appHostPath: hostPath })]);

        const result = provider.findAppHostElement(docPath);

        assert.ok(result, 'Expected to find an AppHostItem via directory match');
        provider.dispose();
    });

    test('matches an AppHostItem when Windows path casing differs', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const hostPath = '/repo/apphost/apphost.csproj';
        const docPath = '/repo/AppHost/AppHost.cs';
        const provider = makeTreeProvider([makeAppHost({ appHostPath: hostPath })]);

        try {
            const result = provider.findAppHostElement(docPath);

            assert.ok(result, 'Expected to find an AppHostItem via case-insensitive Windows path match');
        } finally {
            provider.dispose();
            platformStub.restore();
        }
    });

    test('returns undefined when AppHost lives in a different directory', () => {
        const provider = makeTreeProvider([makeAppHost({ appHostPath: '/elsewhere/Other.csproj' })]);

        const result = provider.findAppHostElement('/repo/AppHost/AppHost.cs');

        assert.strictEqual(result, undefined);
        provider.dispose();
    });

    test('findResourceElement can scope duplicate resource names to an AppHost path', () => {
        const firstHostPath = '/repo/apps/Store/AppHost.csproj';
        const secondHostPath = '/repo/samples/Store/AppHost.csproj';
        const provider = makeTreeProvider([
            makeAppHost({ appHostPath: firstHostPath, appHostPid: 1234, resources: [makeResource({ name: 'cache-a', displayName: 'cache' })] }),
            makeAppHost({ appHostPath: secondHostPath, appHostPid: 5678, resources: [makeResource({ name: 'cache-b', displayName: 'cache' })] }),
        ]);

        const result = provider.findResourceElement('cache', secondHostPath) as any;

        assert.ok(result, 'Expected to find resource in the scoped AppHost');
        assert.strictEqual(result.resource.name, 'cache-b');
        provider.dispose();
    });

    test('matches WorkspaceResourcesItem by exact path (workspace mode)', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [makeResource()],
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const result = provider.findAppHostElement(hostPath);

        assert.ok(result, 'Expected to find a WorkspaceResourcesItem');
        assert.strictEqual(result.contextValue, 'workspaceResources');
        provider.dispose();
    });

    test('matches WorkspaceResourcesItem by same-directory path (workspace mode)', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [makeResource()],
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const result = provider.findAppHostElement('/repo/AppHost/AppHost.cs');

        assert.ok(result, 'Expected to find a WorkspaceResourcesItem via directory match');
        provider.dispose();
    });

    test('workspace mode renders single non-running AppHost candidate without a grouping node', () => {
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: '/repo/AppHost/AppHost.csproj',
            workspaceAppHostCandidatePaths: ['/repo/AppHost/AppHost.csproj'],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        // A single AppHost is surfaced directly at the root with no "Workspace AppHosts"
        // grouping node (https://github.com/microsoft/aspire/issues/18420).
        const topLevel = provider.getChildren();
        assert.strictEqual(topLevel.length, 1);
        const appHostItem = topLevel[0];
        const result = provider.findAppHostElement('/repo/AppHost/AppHost.csproj');

        assert.strictEqual(appHostItem.label, 'AppHost.csproj');
        assert.strictEqual(appHostItem.contextValue, 'workspaceAppHost');
        assert.strictEqual(appHostItem.collapsibleState, vscode.TreeItemCollapsibleState.Expanded);
        // Deploy, publish, and pipeline rows stay hidden until the AppHost's CLI resolves.
        const appHostChildren = provider.getChildren(appHostItem);
        assert.deepStrictEqual(appHostChildren.map(item => item.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostPath',
        ]);
        assert.deepStrictEqual(appHostChildren.map(item => item.command?.command), [
            'aspire-vscode.openAppHostSource',
            'aspire-vscode.runAppHost',
            'aspire-vscode.debugAppHost',
            'aspire-vscode.copyAppHostPath',
        ]);
        // Clicking the Path row copies the AppHost path via the same handler as the right-click
        // context menu, so its command must carry the parent AppHost item as its argument
        // (https://github.com/microsoft/aspire/issues/18578).
        const pathItem = appHostChildren.find(item => item.contextValue === 'workspaceAppHostPath');
        assert.ok(pathItem, 'Expected a Path tree item under the workspace AppHost.');
        assert.deepStrictEqual(pathItem.command?.arguments, [appHostItem]);
        // findAppHostElement rebuilds the tree (getChildren is not cached), so the returned
        // element is a fresh instance. Match by stable id/contextValue rather than reference.
        assert.ok(result, 'Expected to find the workspace AppHost candidate');
        assert.strictEqual(result?.id, appHostItem.id);
        assert.strictEqual(result?.contextValue, 'workspaceAppHost');
        provider.dispose();
    });

    test('workspace mode renders non-running AppHost candidates from aspire ls', () => {
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [
                '/repo/apps/Store/AppHost.csproj',
                '/repo/samples/Store/AppHost.csproj',
            ],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const topLevel = provider.getChildren();
        assert.strictEqual(topLevel.length, 1);
        assert.strictEqual(topLevel[0].contextValue, 'workspaceAppHostsGroup');

        const appHostItems = provider.getChildren(topLevel[0]);

        assert.deepStrictEqual(appHostItems.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
        assert.deepStrictEqual(appHostItems.map(item => item.contextValue), [
            'workspaceAppHost',
            'workspaceAppHost',
        ]);
        provider.dispose();
    });

    test('workspace mode gives case-distinct Windows AppHosts unique rendered IDs', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const upperCasePath = '/workspace/AppHost/apphost.mts';
        const lowerCasePath = '/workspace/apphost/apphost.mts';
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [upperCasePath, lowerCasePath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        try {
            const [group] = provider.getChildren();
            const appHostItems = provider.getChildren(group);

            assert.deepStrictEqual(appHostItems.map(item => item.label), [
                'AppHost/apphost.mts',
                'apphost/apphost.mts',
            ]);
            assert.strictEqual(new Set(appHostItems.map(item => item.id)).size, 2);
        } finally {
            provider.dispose();
            platformStub.restore();
        }
    });

    test('workspace mode does not associate a case-distinct Windows candidate with the wrong running AppHost', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.basename(path.dirname(String(filePath))) === 'AppHost' ? 100n : 101n,
        }) as fs.BigIntStats);
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const upperCasePath = '/workspace/AppHost/apphost.mts';
        const lowerCasePath = '/workspace/apphost/apphost.mts';
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [makeAppHost({ appHostPath: upperCasePath, resources: [] })],
            workspaceResources: [],
            workspaceAppHostPath: upperCasePath,
            workspaceAppHostCandidatePaths: [upperCasePath, lowerCasePath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        try {
            const topLevelItems = provider.getChildren();

            assert.strictEqual(topLevelItems.length, 2);
            assert.ok(topLevelItems[0].contextValue?.startsWith('workspaceResources'));
            assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHost');
            assert.strictEqual((topLevelItems[1] as vscode.TreeItem & { appHostPath: string }).appHostPath, lowerCasePath);
        } finally {
            provider.dispose();
            statStub.restore();
            platformStub.restore();
        }
    });

    test('loading hides stale AppHosts', () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        for (const viewMode of ['workspace', 'global'] as const) {
            const repository = {
                viewMode,
                isLoading: true,
                appHosts: [makeAppHost({ appHostPath })],
                workspaceResources: [],
                workspaceAppHostPath: undefined,
                workspaceAppHostCandidatePaths: [],
                workspaceAppHostName: undefined,
                onDidChangeData,
            } as unknown as AppHostDataRepository;
            const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

            assert.deepStrictEqual(provider.getChildren(), []);
            provider.dispose();
        }
    });

    test('workspace mode renders launching AppHost with spinner and no context menu', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const launchService = makeLaunchService();

        const resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: true, source: 'path' });
        const stub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        try {
            await launchService.launch(appHostPath, 'run', true);
        }
        finally {
            stub.restore();
            resolveCliPathStub.restore();
        }

        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);

        // A single launching AppHost is surfaced directly at the root with no grouping node.
        const [item] = provider.getChildren();

        assert.ok(item, 'Expected a launching workspace AppHost item');
        assert.strictEqual(item.contextValue, 'workspaceAppHostLaunching');
        assert.deepStrictEqual((item.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        provider.dispose();
    });

    test('workspace mode groups both running and idle AppHosts under their group nodes when each has two or more', () => {
        const runningPath = '/repo/apps/Store/AppHost.csproj';
        const secondRunningPath = '/repo/apps/Api/AppHost.csproj';
        const idlePath = '/repo/apps/Backend/AppHost.csproj';
        const secondIdlePath = '/repo/apps/Web/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: runningPath, appHostPid: 1234, cliPid: 5678, resources: [makeResource()] }),
                makeAppHost({ appHostPath: secondRunningPath, appHostPid: 4321, cliPid: 8765, resources: [makeResource()] }),
            ],
            workspaceResources: [],
            workspaceAppHostPath: runningPath,
            workspaceAppHostCandidatePaths: [runningPath, secondRunningPath, idlePath, secondIdlePath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const topLevelItems = provider.getChildren();

        // With two or more running AND two or more idle AppHosts, both sets are wrapped in
        // sibling groups so they nest at the same depth and read symmetrically in the tree.
        assert.strictEqual(topLevelItems.length, 2);
        assert.strictEqual(topLevelItems[0].contextValue, 'runningAppHostsGroup');
        assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHostsGroup');

        // Running group contains the two running AppHosts (rendered as nested AppHostItems)
        const runningChildren = provider.getChildren(topLevelItems[0]);
        assert.strictEqual(runningChildren.length, 2);
        assert.deepStrictEqual(runningChildren.map(item => item.contextValue), ['appHost', 'appHost']);

        // Workspace group contains the two idle AppHosts
        const idleChildren = provider.getChildren(topLevelItems[1]);
        assert.strictEqual(idleChildren.length, 2);
        assert.deepStrictEqual(idleChildren.map(item => item.contextValue), ['workspaceAppHost', 'workspaceAppHost']);
        provider.dispose();
    });

    test('workspace mode surfaces a single idle AppHost directly when a running AppHost exists', () => {
        const runningPath = '/repo/apps/Store/AppHost.csproj';
        const idlePath = '/repo/apps/Backend/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [makeAppHost({ appHostPath: runningPath, appHostPid: 1234, cliPid: 5678, resources: [makeResource()] })],
            workspaceResources: [],
            workspaceAppHostPath: runningPath,
            workspaceAppHostCandidatePaths: [runningPath, idlePath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const topLevelItems = provider.getChildren();

        // A single running AppHost and a single idle AppHost are each surfaced directly,
        // with no "Running AppHosts (1)" or "Workspace AppHosts (1)" wrapper
        // (https://github.com/microsoft/aspire/issues/18420).
        assert.strictEqual(topLevelItems.length, 2);
        assert.ok(topLevelItems[0].contextValue?.startsWith('workspaceResources'));
        assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHost');
        provider.dispose();
    });

    test('workspace mode matches running AppHost to candidate by directory when paths differ', () => {
        // aspire ls returns the project file (.csproj) while aspire ps can report the
        // AppHost source file (Program.cs) in the same directory. These paths are not
        // equal, but the tree should still pair them as the SAME AppHost via the
        // directory-equivalence fallback in isMatchingAppHostPath.
        const candidateCsproj = '/repo/apps/Store/AppHost.csproj';
        const runningSourceFile = '/repo/apps/Store/Program.cs';
        const idlePath = '/repo/apps/Backend/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [makeAppHost({ appHostPath: runningSourceFile, appHostPid: 1234, cliPid: 5678, resources: [] })],
            workspaceResources: [makeResource({ name: 'workspace-service' })],
            workspaceAppHostPath: candidateCsproj,
            workspaceAppHostCandidatePaths: [candidateCsproj, idlePath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const topLevelItems = provider.getChildren();

        // The .csproj candidate should be recognized as running (surfaced directly as a
        // flat WorkspaceResourcesItem since it's the only running AppHost), and the
        // unrelated lone idle candidate is surfaced directly as a sibling. Without
        // directory-equivalence matching, the .csproj candidate would be misclassified as idle.
        assert.strictEqual(topLevelItems.length, 2);
        assert.ok(topLevelItems[0].contextValue?.startsWith('workspaceResources'));
        assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHost');

        const resourceChildren = provider.getChildren(topLevelItems[0]);
        assert.strictEqual(resourceChildren.length, 1);
        assert.strictEqual(resourceChildren[0].label, 'workspace-service');
        provider.dispose();
    });

    test('runAppHost shows warning when element is undefined', async () => {
        const provider = makeTreeProvider([], 'workspace');
        const stub = sinon.stub(vscode.window, 'showWarningMessage');

        await provider.runAppHost(undefined, true);

        assert.ok(stub.calledOnce, 'Expected a warning message');
        stub.restore();
        provider.dispose();
    });

    test('runAppHost delegates to launch service with correct path', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const launchService = makeLaunchService();
        const launchStub = sinon.stub(launchService, 'launch').resolves();
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);

        // A single candidate is surfaced directly at the root (no grouping node); pass it to runAppHost.
        const [item] = provider.getChildren();
        await provider.runAppHost(item as any, false);

        assert.ok(launchStub.calledOnce, 'Expected launch to be called');
        assert.strictEqual(launchStub.firstCall.args[0], appHostPath);
        assert.strictEqual(launchStub.firstCall.args[1], 'run');
        assert.strictEqual(launchStub.firstCall.args[2], false);
        launchStub.restore();
        provider.dispose();
    });

    test('selected AppHost actions map commands and CLI identity to the secondary AppHost', async () => {
        const primaryPath = '/repo/primary/AppHost/AppHost.csproj';
        const secondaryPath = '/repo/secondary/AppHost/AppHost.csproj';
        const secondaryFolder = createWorkspaceFolder('secondary', '/repo/secondary');
        const secondaryTarget = workspaceFolderCliPathTarget(secondaryFolder);
        const cliPath = '/repo/secondary/tools/aspire';
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake(uri =>
            uri.path.startsWith(`${secondaryFolder.uri.path}/`) ? secondaryFolder : undefined);
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: primaryPath,
            workspaceAppHostCandidatePaths: [primaryPath, secondaryPath],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath,
            available: true,
            source: 'configured',
        });
        const checkCliAvailableStub = sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (_operation, _target, options) => ({
                cliPath: options?.pinnedCliPath ?? cliPath,
                available: true,
            }));
        const terminalProvider = {} as AspireTerminalProvider;
        const launchService = makeLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getCapabilityStatus').resolves('supported');
        const getConfigInfoStub = sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            localSettingsPath: '/repo/secondary/aspire.config.json',
            globalSettingsPath: '/repo/global-aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [pipelineInteractionCapability],
        });
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const callbacks = registerTreeCommandCallbacks(sandbox, provider, repository);
        const [workspaceAppHostsGroup] = provider.getChildren();
        await waitForCondition(
            () => provider.getChildren(provider.getChildren()[0])[1].contextValue === 'workspaceAppHost:canDeploy:canPublish:canDo',
            'Expected the secondary AppHost to report its probed actions.');
        const secondaryAppHost = provider.getChildren(workspaceAppHostsGroup)[1];
        assert.ok(secondaryAppHost instanceof WorkspaceAppHostItem);

        await callbacks.get('aspire-vscode.deployAppHost')!(secondaryAppHost);
        await callbacks.get('aspire-vscode.publishAppHost')!(secondaryAppHost);
        await callbacks.get('aspire-vscode.runPipelineStepAppHost')!(secondaryAppHost);
        await callbacks.get('aspire-vscode.debugPipelineStepAppHost')!(secondaryAppHost);

        // Each AppHost resolves its own CLI once while rendering, and the four actions reuse that
        // exact pair instead of resolving again. Pipeline actions check the pinned executable and
        // capability set again after the CLI-owned step selection or legacy input prompt completes.
        assert.deepStrictEqual(resolveCliPathStub.getCalls().map(call => call.args), [
            [windowCliPathTarget],
            [secondaryTarget],
        ]);
        assert.deepStrictEqual(checkCliAvailableStub.getCalls().map(call => call.args), [
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
            ['debug_gate', secondaryTarget, { pinnedCliPath: cliPath }],
        ]);
        assert.deepStrictEqual(getConfigInfoStub.getCalls().map(call => call.args), [
            [{ target: secondaryTarget, cliPath, suppressErrors: true, forceRefresh: true }],
            [{ target: secondaryTarget, cliPath, suppressErrors: true, forceRefresh: true }],
            [{ target: secondaryTarget, cliPath, suppressErrors: true, forceRefresh: true }],
            [{ target: secondaryTarget, cliPath, suppressErrors: true, forceRefresh: true }],
        ]);
        assert.deepStrictEqual(launchStub.getCalls().map(call => call.args), [
            [secondaryPath, 'deploy', false, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'publish', false, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'do', true, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'do', false, undefined, secondaryTarget, cliPath],
        ]);
        provider.dispose();
    });

    test('selected AppHost action handlers resolve every actionable tree item type', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const targetFolder = createWorkspaceFolder('repo', '/repo');
        const target = workspaceFolderCliPathTarget(targetFolder);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(targetFolder);
        sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: '/repo/tools/aspire',
            available: true,
            source: 'configured',
        });
        sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (_operation, _target, options) => ({
                cliPath: options?.pinnedCliPath ?? '/repo/tools/aspire',
                available: true,
            }));
        const terminalProvider = {
            resolveAspireCliPath: sandbox.stub().resolves({
                cliPath: '/repo/tools/aspire',
                available: true,
                source: 'configured',
            }),
        } as unknown as AspireTerminalProvider;
        const launchService = makeLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getCapabilityStatus').resolves('supported');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const globalRepository = {
            viewMode: 'global' as ViewMode,
            appHosts: [makeAppHost({ appHostPath })],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const globalProvider = new AspireAppHostTreeProvider(globalRepository, terminalProvider, launchService);
        const [appHostItem] = globalProvider.getChildren();
        assert.ok(appHostItem instanceof AppHostItem);
        const workspaceResourcesRepository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [makeAppHost({ appHostPath, resources: [] })],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({ appHostPath, resources: [] }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const workspaceResourcesProvider = new AspireAppHostTreeProvider(workspaceResourcesRepository, terminalProvider, launchService);
        const [workspaceResourcesItem] = workspaceResourcesProvider.getChildren();
        assert.ok(workspaceResourcesItem instanceof WorkspaceResourcesItem);
        const workspaceAppHostRepository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const workspaceAppHostProvider = new AspireAppHostTreeProvider(workspaceAppHostRepository, terminalProvider, launchService);
        const [workspaceAppHostItem] = workspaceAppHostProvider.getChildren();
        assert.ok(workspaceAppHostItem instanceof WorkspaceAppHostItem);

        await globalProvider.deployAppHost(appHostItem);
        await workspaceResourcesProvider.publishAppHost(workspaceResourcesItem);
        await workspaceAppHostProvider.deployAppHost(workspaceAppHostItem);

        assert.deepStrictEqual(launchStub.getCalls().map(call => call.args), [
            [appHostPath, 'deploy', false, undefined, target, '/repo/tools/aspire'],
            [appHostPath, 'publish', false, undefined, target, '/repo/tools/aspire'],
            [appHostPath, 'deploy', false, undefined, target, '/repo/tools/aspire'],
        ]);
        globalProvider.dispose();
        workspaceResourcesProvider.dispose();
        workspaceAppHostProvider.dispose();
    });

    test('selected AppHost pipeline cancellation returns without launch or error toast', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const targetFolder = createWorkspaceFolder('repo', '/repo');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(targetFolder);
        sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: '/repo/tools/aspire',
            available: true,
            source: 'configured',
        });
        sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (_operation, _target, options) => ({
                cliPath: options?.pinnedCliPath ?? '/repo/tools/aspire',
                available: true,
            }));
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            resolveAspireCliPath: sandbox.stub().resolves({
                cliPath: '/repo/tools/aspire',
                available: true,
                source: 'configured',
            }),
        } as unknown as AspireTerminalProvider;
        const launchService = makeLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getCapabilityStatus').resolves('supported');
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            localSettingsPath: '/repo/aspire.config.json',
            globalSettingsPath: '/repo/global-aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [],
        });
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const callbacks = registerTreeCommandCallbacks(sandbox, provider, repository);
        const [appHostItem] = provider.getChildren();

        await callbacks.get('aspire-vscode.runPipelineStepAppHost')!(appHostItem);

        assert.strictEqual(launchStub.called, false);
        assert.strictEqual(showErrorMessageStub.called, false);
        provider.dispose();
    });

    test('selected AppHost launch errors propagate once without a provider error toast', async () => {
        const launchError = new Error('launch failed');
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const targetFolder = createWorkspaceFolder('repo', '/repo');
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(targetFolder);
        sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: '/repo/tools/aspire',
            available: true,
            source: 'configured',
        });
        sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (_operation, _target, options) => ({
                cliPath: options?.pinnedCliPath ?? '/repo/tools/aspire',
                available: true,
            }));
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            resolveAspireCliPath: sandbox.stub().resolves({
                cliPath: '/repo/tools/aspire',
                available: true,
                source: 'configured',
            }),
        } as unknown as AspireTerminalProvider;
        const launchService = makeLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').rejects(launchError);
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getCapabilityStatus').resolves('supported');
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const callbacks = registerTreeCommandCallbacks(sandbox, provider, repository);
        const [appHostItem] = provider.getChildren();

        await assert.rejects(
            callbacks.get('aspire-vscode.deployAppHost')!(appHostItem),
            error => error === launchError);

        assert.strictEqual(launchStub.callCount, 1);
        assert.strictEqual(showErrorMessageStub.called, false);
        provider.dispose();
    });

    test('selected AppHost actions stop at the CLI availability gate', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const targetFolder = createWorkspaceFolder('repo', '/repo');
        const target = workspaceFolderCliPathTarget(targetFolder);
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(targetFolder);
        const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: 'aspire',
            available: false,
            source: 'not-found',
        });
        const terminalProvider = {} as AspireTerminalProvider;
        const launchService = makeLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        const capabilityStub = sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'hasCapability').resolves(true);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').resolves('deploy');
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const callbacks = registerTreeCommandCallbacks(sandbox, provider, repository);
        const [appHostItem] = provider.getChildren();

        await callbacks.get('aspire-vscode.deployAppHost')!(appHostItem);
        await callbacks.get('aspire-vscode.runPipelineStepAppHost')!(appHostItem);

        // The silent render probe resolves the CLI once; each explicit action then re-resolves
        // through the availability gate so the user is told the CLI is missing.
        assert.deepStrictEqual(resolveCliPathStub.getCalls().map(call => call.args), [[target], [target], [target]]);
        assert.strictEqual(showErrorMessageStub.callCount, 2);
        assert.strictEqual(capabilityStub.called, false);
        assert.strictEqual(showInputBoxStub.called, false);
        assert.strictEqual(launchStub.called, false);
        provider.dispose();
    });

    test('runAppHost surfaces launch errors via showErrorMessage', async () => {
        // The previous fire-and-forget call discarded rejections — they surfaced as
        // unhandled promise rejections with no user feedback. The async variant must
        // catch and report so the user knows the launch failed.
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const launchService = makeLaunchService();
        const launchStub = sinon.stub(launchService, 'launch').rejects(new Error('startDebugging blew up'));
        const errorStub = sinon.stub(vscode.window, 'showErrorMessage');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), launchService);

        const [item] = provider.getChildren();
        await assert.rejects(provider.runAppHost(item as any, false), /startDebugging blew up/);

        assert.ok(launchStub.calledOnce, 'Expected launch to be called');
        assert.ok(errorStub.calledOnce, 'Expected showErrorMessage to be called when launch rejects');
        launchStub.restore();
        errorStub.restore();
        provider.dispose();
    });

    test('workspace mode renders a running AppHost with no resources', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({
                appHostPath: hostPath,
                appHostPid: 1234,
                cliPid: 5678,
                dashboardUrl: 'https://localhost:17193/login?t=token',
                logFilePath: '/tmp/apphost.log',
                resources: [],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostCandidatePaths: [hostPath],
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [appHostItem] = provider.getChildren();
        const appHostChildren = provider.getChildren(appHostItem);
        const result = provider.findAppHostElement(hostPath);

        assert.ok(appHostItem, 'Expected a workspace AppHost item');
        assert.strictEqual(appHostItem.label, 'AppHost.csproj');
        assert.strictEqual(appHostItem.contextValue, 'workspaceResources:hasAppHost');
        assert.strictEqual(appHostChildren.length, 2);
        assert.ok(result, 'Expected to find the zero-resource workspace AppHost');
        provider.dispose();
    });

    test('workspace mode renders running workspace AppHosts from ps', () => {
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: '/repo/apps/Store/AppHost.csproj', appHostPid: 1234 }),
                makeAppHost({ appHostPath: '/repo/samples/Store/AppHost.csproj', appHostPid: 5678 }),
            ],
            workspaceResources: [],
            workspaceAppHost: undefined,
            workspaceAppHostPath: undefined,
            workspaceAppHostName: undefined,
            workspaceAppHostCandidatePaths: ['/repo/apps/Store/AppHost.csproj', '/repo/samples/Store/AppHost.csproj'],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const appHostItems = provider.getChildren();

        assert.deepStrictEqual(appHostItems.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
        provider.dispose();
    });

    test('workspace resource commands use the AppHost that owns the resource', async () => {
        const commands: Array<{ command: AspireSubcommand; options: unknown }> = [];
        const runResourceCommandCalls: Array<[string, string | undefined, string, readonly string[]]> = [];
        const selectedHostPath = '/repo/apps/Store/AppHost.csproj';
        const otherHostPath = '/repo/samples/Store/AppHost.csproj';
        const otherFolder = {
            uri: vscode.Uri.file('/repo/samples'),
            name: 'samples',
            index: 1,
        };
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) => uri.fsPath.startsWith(`${otherFolder.uri.fsPath}${path.sep}`) ? otherFolder : undefined);
        const otherTarget = workspaceFolderCliPathTarget(otherFolder);
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: [makeResource({ name: 'cache-a', displayName: 'cache' })] }),
                makeAppHost({ appHostPath: otherHostPath, appHostPid: 5678, resources: [makeResource({ name: 'cache-b', displayName: 'cache' })] }),
            ],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: [] }),
            workspaceAppHostPath: selectedHostPath,
            workspaceAppHostName: 'apps/Store/AppHost.csproj',
            workspaceAppHostCandidatePaths: [selectedHostPath, otherHostPath],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
            runResourceCommand: async (resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = []) => {
                runResourceCommandCalls.push([resourceName, appHostPath, commandName, additionalArgs]);
                return { stdout: '', stderr: '' };
            },
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand, _showTerminal?: boolean, _additionalArgs?: string[], options?: unknown) => commands.push({ command, options }),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());

        const otherAppHostItem = provider.getChildren()[1];
        const resourcesGroup = provider.getChildren(otherAppHostItem).find(child => child.label === 'Resources');
        assert.ok(resourcesGroup, 'Expected resources group for second AppHost');
        const resourceItem = provider.getChildren(resourcesGroup)[0];

        provider.viewResourceLogs(resourceItem as any);
        provider.openResourceTerminal(resourceItem as any);
        await provider.restartResource(resourceItem as any);

        // Logs and terminal still go through the terminal; restart now runs over the hidden CLI
        // backchannel and must target the AppHost that owns the resource.
        assert.deepStrictEqual(commands, [
            {
                command: ['logs', shellArg('cache'), '--apphost', shellArg(otherHostPath)],
                options: { target: otherTarget },
            },
            {
                command: ['terminal', 'attach', shellArg('cache-b'), '--apphost', shellArg(otherHostPath)],
                options: { terminalTarget: 'editor', target: otherTarget },
            },
        ]);
        assert.deepStrictEqual(runResourceCommandCalls, [['cache-b', otherHostPath, 'restart', []]]);
        provider.dispose();
        getWorkspaceFolderStub.restore();
    });

    test('workspace resource commands use the running AppHost path when no workspace AppHost is selected', async () => {
        const commands: AspireSubcommand[] = [];
        const runResourceCommandCalls: Array<[string, string | undefined, string, readonly string[]]> = [];
        const runningHostPath = '/repo/apps/Store/AppHost.csproj';
        const idleHostPath = '/repo/samples/Store/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: runningHostPath, appHostPid: 1234, resources: [makeResource({ name: 'cache', displayName: 'cache' })] }),
            ],
            workspaceResources: [],
            workspaceAppHost: undefined,
            workspaceAppHostPath: undefined,
            workspaceAppHostName: undefined,
            workspaceAppHostCandidatePaths: [runningHostPath, idleHostPath],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
            runResourceCommand: async (resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = []) => {
                runResourceCommandCalls.push([resourceName, appHostPath, commandName, additionalArgs]);
                return { stdout: '', stderr: '' };
            },
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());

        const [runningAppHostItem] = provider.getChildren();
        const resourceItem = provider.getChildren(runningAppHostItem)[0];

        provider.viewResourceLogs(resourceItem as any);
        provider.openResourceTerminal(resourceItem as any);
        await provider.restartResource(resourceItem as any);

        assert.deepStrictEqual(commands, [
            ['logs', shellArg('cache'), '--apphost', shellArg(runningHostPath)],
            ['terminal', 'attach', shellArg('cache'), '--apphost', shellArg(runningHostPath)],
        ]);
        assert.deepStrictEqual(runResourceCommandCalls, [['cache', runningHostPath, 'restart', []]]);
        provider.dispose();
    });

    test('openResourceTerminal adds replica when terminal metadata includes index', async () => {
        const commands: AspireSubcommand[] = [];
        const appHostPath = '/repo/apps/Store/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({
                appHostPath,
                resources: [makeResource({
                    name: 'cache',
                    properties: {
                        'terminal.enabled': 'true',
                        'terminal.replicaIndex': '2',
                    },
                })],
            }),
            workspaceAppHostPath: appHostPath,
            workspaceAppHostName: 'apps/Store/AppHost.csproj',
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());

        const [workspaceItem] = provider.getChildren();
        const [resourceItem] = provider.getChildren(workspaceItem);
        await provider.openResourceTerminal(resourceItem as any);

        assert.deepStrictEqual(commands, [
            ['terminal', 'attach', shellArg('cache'), '--apphost', shellArg(appHostPath), '--replica', shellArg('2')],
        ]);
        provider.dispose();
    });

    test('workspace mode uses describe resources for selected AppHost when ps has no resources', () => {
        const selectedHostPath = '/repo/apps/Store/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: undefined }),
                makeAppHost({ appHostPath: '/repo/samples/Store/AppHost.csproj', appHostPid: 5678, resources: undefined }),
            ],
            workspaceResources: [makeResource({ name: 'api', displayName: 'api' })],
            workspaceAppHost: makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: undefined }),
            workspaceAppHostPath: selectedHostPath,
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostCandidatePaths: [selectedHostPath, '/repo/samples/Store/AppHost.csproj'],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [selectedAppHostItem] = provider.getChildren();
        const selectedChildren = provider.getChildren(selectedAppHostItem);
        const resourcesGroup = selectedChildren.find(child => child.label === 'Resources');

        assert.ok(resourcesGroup, 'Expected selected AppHost to use describe resources when ps has no resources');
        assert.deepStrictEqual(provider.getChildren(resourcesGroup).map(child => child.label), ['api']);
        provider.dispose();
    });

    test('workspace mode uses describe resources for selected AppHost when ps has empty resources', () => {
        const selectedHostPath = '/repo/apps/Store/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [
                makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: [] }),
                makeAppHost({ appHostPath: '/repo/samples/Store/AppHost.csproj', appHostPid: 5678, resources: [] }),
            ],
            workspaceResources: [makeResource({ name: 'api', displayName: 'api' })],
            workspaceAppHost: makeAppHost({ appHostPath: selectedHostPath, appHostPid: 1234, resources: [] }),
            workspaceAppHostPath: selectedHostPath,
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostCandidatePaths: [selectedHostPath, '/repo/samples/Store/AppHost.csproj'],
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [selectedAppHostItem] = provider.getChildren();
        const selectedChildren = provider.getChildren(selectedAppHostItem);
        const resourcesGroup = selectedChildren.find(child => child.label === 'Resources');

        assert.ok(resourcesGroup, 'Expected selected AppHost to use describe resources when ps resources are empty');
        assert.deepStrictEqual(provider.getChildren(resourcesGroup).map(child => child.label), ['api']);
        provider.dispose();
    });

    test('workspace mode renders ps resources before describe resources arrive', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({
                appHostPath: hostPath,
                appHostPid: 1234,
                cliPid: 5678,
                dashboardUrl: 'https://localhost:17193/login?t=token',
                logFilePath: '/tmp/apphost.log',
                resources: [
                    makeResource({ name: 'api', displayName: 'api' }),
                    makeResource({ name: 'api-child', displayName: 'api-child', properties: { 'resource.parentName': 'api' } }),
                ],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [appHostItem] = provider.getChildren();
        const appHostChildren = provider.getChildren(appHostItem);

        assert.ok(appHostItem, 'Expected a workspace AppHost item');
        const apiItem = appHostChildren.find(child => child.label === 'api');
        assert.ok(apiItem);
        assert.ok(provider.getChildren(apiItem).some(child => child.label === 'api-child'));
        provider.dispose();
    });

    test('matches the right AppHostItem when multiple are tracked', () => {
        const hostA = '/repo/A/A.csproj';
        const hostB = '/repo/B/B.csproj';
        const provider = makeTreeProvider([
            makeAppHost({ appHostPath: hostA, appHostPid: 111 }),
            makeAppHost({ appHostPath: hostB, appHostPid: 222 }),
        ]);

        const resultA = provider.findAppHostElement('/repo/A/AppHost.cs');
        const resultB = provider.findAppHostElement(hostB);

        assert.ok(resultA);
        assert.ok(resultB);
        assert.notStrictEqual(resultA, resultB, 'Expected distinct items for distinct AppHosts');
        provider.dispose();
    });

    test('resource command quick pick orders commands by registration order', async () => {
        const sandbox = sinon.createSandbox();
        const resource = makeResource({
            commands: {
                'set-parameter': { displayName: 'Set parameter', description: null, sortOrder: 0 },
                'custom-action': { displayName: 'Custom action', description: null, sortOrder: 1 },
                'delete-parameter': { displayName: 'Delete parameter', description: null, sortOrder: 2 },
            },
        });
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [resource],
            }),
        ]);

        try {
            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
            const element = provider.findResourceElement('my-service');
            assert.ok(element, 'Expected to find resource element');

            await assert.rejects(provider.executeResourceCommand(element as never), /Canceled/);

            const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
            assert.deepStrictEqual(items.map(item => item.label), [
                'set-parameter',
                'custom-action',
                'delete-parameter',
            ]);
        } finally {
            sandbox.restore();
            provider.dispose();
        }
    });

    test('parameter missing value tooltip uses humanized state', () => {
        const resource = makeResource({
            resourceType: 'Parameter',
            state: ResourceState.ValueMissing,
        });
        const provider = makeTreeProvider([
            makeAppHost({
                resources: [resource],
            }),
        ]);
        const [appHostItem] = provider.getChildren();
        const resourcesGroup = provider.getChildren(appHostItem).find(child => child.contextValue === 'resourcesGroup');
        assert.ok(resourcesGroup, 'Expected resources group');
        const [resourceItem] = provider.getChildren(resourcesGroup);
        const tooltip = resourceItem.tooltip as vscode.MarkdownString;

        assert.ok(tooltip.value.includes('State: Value missing'), tooltip.value);
        provider.dispose();
    });
});

suite('LogFileItem in tree', () => {
    test('global mode shows LogFileItem when logFilePath is set', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/repo/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:18888',
                logFilePath: '/tmp/apphost.log',
                resources: [],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.ok(logItem, 'Expected a LogFileItem in global tree');
        assert.strictEqual(logItem.tooltip, '/tmp/apphost.log');
        assert.strictEqual((logItem as any).logFilePath, '/tmp/apphost.log');
        provider.dispose();
    });

    test('global mode does not show LogFileItem when logFilePath is null', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/repo/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:18888',
                logFilePath: null,
                resources: [],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.strictEqual(logItem, undefined);
        provider.dispose();
    });

    test('global mode does not show LogFileItem when logFilePath is undefined', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/repo/AppHost.csproj',
                appHostPid: 1,
                dashboardUrl: 'http://localhost:18888',
                resources: [],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.strictEqual(logItem, undefined);
        provider.dispose();
    });

    test('workspace mode shows LogFileItem when logFilePath is set', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({
                appHostPath: hostPath,
                appHostPid: 1234,
                logFilePath: '/var/log/aspire.log',
                resources: [],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.ok(logItem, 'Expected a LogFileItem in workspace tree');
        assert.strictEqual(logItem.tooltip, '/var/log/aspire.log');
        provider.dispose();
    });

    test('workspace mode does not show LogFileItem when logFilePath is absent', () => {
        const hostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHost: makeAppHost({
                appHostPath: hostPath,
                appHostPid: 1234,
                resources: [],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.strictEqual(logItem, undefined);
        provider.dispose();
    });

    test('LogFileItem has correct command to open log file', () => {
        const provider = makeTreeProvider([
            makeAppHost({
                appHostPath: '/repo/AppHost.csproj',
                appHostPid: 1,
                logFilePath: '/tmp/my-app.log',
                resources: [],
            }),
        ]);

        const [appHostItem] = provider.getChildren();
        const children = provider.getChildren(appHostItem);
        const logItem = children.find(c => c.contextValue === 'logFileItem');

        assert.ok(logItem);
        assert.ok(logItem.command);
        assert.strictEqual(logItem.command.command, 'aspire-vscode.viewAppHostLogFile');
        assert.deepStrictEqual(logItem.command.arguments, ['/tmp/my-app.log']);
        provider.dispose();
    });
});

suite('viewAppHostLogFile', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('shows warning when element is null', async () => {
        const provider = makeTreeProvider([]);
        const openTextDocStub = sandbox.stub(vscode.workspace, 'openTextDocument');
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

        await provider.viewAppHostLogFile(null);

        assert.strictEqual(openTextDocStub.called, false);
        assert.ok(warningStub.calledOnce);
        provider.dispose();
    });

    test('shows warning when element is empty string', async () => {
        const provider = makeTreeProvider([]);
        const openTextDocStub = sandbox.stub(vscode.workspace, 'openTextDocument');
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

        await provider.viewAppHostLogFile('');

        assert.strictEqual(openTextDocStub.called, false);
        assert.ok(warningStub.calledOnce);
        provider.dispose();
    });

    test('shows warning when element is a number', async () => {
        const provider = makeTreeProvider([]);
        const openTextDocStub = sandbox.stub(vscode.workspace, 'openTextDocument');
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

        await provider.viewAppHostLogFile(42);

        assert.strictEqual(openTextDocStub.called, false);
        assert.ok(warningStub.calledOnce);
        provider.dispose();
    });

    test('opens document for valid file path string', async () => {
        const provider = makeTreeProvider([]);
        const fakeDoc = { uri: vscode.Uri.file('/tmp/test.log') } as vscode.TextDocument;
        const openTextDocStub = sandbox.stub(vscode.workspace, 'openTextDocument').resolves(fakeDoc);
        const showTextDocStub = sandbox.stub(vscode.window, 'showTextDocument').resolves(undefined as any);

        await provider.viewAppHostLogFile('/tmp/test.log');

        assert.ok(openTextDocStub.calledOnce);
        assert.ok(showTextDocStub.calledOnce);
        assert.strictEqual(showTextDocStub.firstCall.args[1]?.preview, false);
        provider.dispose();
    });

    test('shows warning when file cannot be opened', async () => {
        const provider = makeTreeProvider([]);
        sandbox.stub(vscode.workspace, 'openTextDocument').rejects(new Error('File not found'));
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

        await provider.viewAppHostLogFile('/nonexistent/path.log');

        assert.ok(warningStub.calledOnce);
        assert.match(warningStub.firstCall.args[0], /File not found/);
        provider.dispose();
    });
});

suite('copyAppHostPath', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('copies the workspace AppHost path and shows a confirmation notification', async () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'Store',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const clipboard = makeClipboard();
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService(), undefined, clipboard);
        try {
            const infoStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);

            const [appHostItem] = provider.getChildren();
            assert.strictEqual(appHostItem.contextValue, 'workspaceAppHost');
            await provider.copyAppHostPath(appHostItem as any);

            assert.strictEqual(clipboard.text, appHostPath);
            assert.strictEqual(infoStub.callCount, 1);
            assert.strictEqual(infoStub.firstCall.args[0], appHostPathCopiedToClipboard);
        } finally {
            provider.dispose();
        }
    });

    test('shows a warning and skips the notification when the AppHost path is missing', async () => {
        const clipboard = makeClipboard();
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostCandidatePaths: [],
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: undefined,
            onDidChangeData: (() => ({ dispose: () => { } })) as vscode.Event<void>,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService(), undefined, clipboard);
        try {
            const infoStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);
            const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

            await provider.copyAppHostPath({ appHostPath: undefined } as any);

            assert.strictEqual(clipboard.text, undefined);
            assert.strictEqual(infoStub.callCount, 0);
            assert.ok(warningStub.calledOnce);
            assert.strictEqual(warningStub.firstCall.args[0], appHostPathInvalid);
        } finally {
            provider.dispose();
        }
    });
});

suite('viewAppHostSource', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('provideTextDocumentContent returns stored JSON', async () => {
        const appHost = makeAppHost({ appHostPid: 999, appHostPath: '/repo/App.csproj' });
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts: [appHost],
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());

        const fakeDoc = { uri: vscode.Uri.parse('aspire-source:AppHost-999.json') } as vscode.TextDocument;
        sandbox.stub(vscode.workspace, 'openTextDocument').resolves(fakeDoc);
        sandbox.stub(vscode.window, 'showTextDocument').resolves(undefined as any);

        // Get the AppHostItem from the tree
        const [appHostItem] = provider.getChildren();
        await provider.viewAppHostSource(appHostItem as any);

        const uri = vscode.Uri.parse('aspire-source:AppHost-999.json');
        const content = provider.provideTextDocumentContent(uri);
        assert.ok(content.length > 0, 'Expected non-empty content');
        const parsed = JSON.parse(content);
        assert.strictEqual(parsed.appHostPid, 999);
        assert.strictEqual(parsed.appHostPath, '/repo/App.csproj');
        provider.dispose();
    });

    test('lazily registers content provider once and updates already opened source document', async () => {
        const appHosts = [
            makeAppHost({ appHostPid: 999, appHostPath: '/repo/App.csproj', dashboardUrl: 'https://old.example' }),
        ];
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'global' as ViewMode,
            appHosts,
            workspaceResources: [],
            workspaceAppHostPath: undefined,
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider(), makeLaunchService());
        const registerStub = sandbox.stub(vscode.workspace, 'registerTextDocumentContentProvider').returns({ dispose: () => { } });
        const fakeDoc = { uri: vscode.Uri.parse('aspire-source:AppHost-999.json') } as vscode.TextDocument;
        sandbox.stub(vscode.workspace, 'openTextDocument').resolves(fakeDoc);
        sandbox.stub(vscode.window, 'showTextDocument').resolves(undefined as any);
        const changedUris: string[] = [];
        const changeSubscription = provider.onDidChange(uri => changedUris.push(uri.toString()));

        assert.strictEqual(registerStub.called, false);

        let [appHostItem] = provider.getChildren();
        await provider.viewAppHostSource(appHostItem as any);

        appHosts[0] = makeAppHost({ appHostPid: 999, appHostPath: '/repo/App.csproj', dashboardUrl: 'https://new.example' });
        [appHostItem] = provider.getChildren();
        await provider.viewAppHostSource(appHostItem as any);

        const uri = vscode.Uri.parse('aspire-source:AppHost-999.json');
        const content = provider.provideTextDocumentContent(uri);
        const parsed = JSON.parse(content);
        assert.strictEqual(parsed.dashboardUrl, 'https://new.example');
        assert.ok(registerStub.calledOnce);
        assert.deepStrictEqual(changedUris, [uri.toString(), uri.toString()]);
        changeSubscription.dispose();
        provider.dispose();
    });

    test('provideTextDocumentContent returns empty string for unknown URI', () => {
        const provider = makeTreeProvider([]);
        const uri = vscode.Uri.parse('aspire-source:Unknown.json');

        const content = provider.provideTextDocumentContent(uri);

        assert.strictEqual(content, '');
        provider.dispose();
    });

    test('shows warning when element is undefined', async () => {
        const provider = makeTreeProvider([]);
        const openTextDocStub = sandbox.stub(vscode.workspace, 'openTextDocument');
        const warningStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined as any);

        await provider.viewAppHostSource(undefined);

        assert.strictEqual(openTextDocStub.called, false);
        assert.ok(warningStub.calledOnce);
        provider.dispose();
    });
});

suite('showResourceCommandOutput', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('uses hidden AppHost path query to disambiguate output documents across global AppHosts', async () => {
        const provider = makeTreeProvider([]);
        const openedUris: vscode.Uri[] = [];

        sandbox.stub(vscode.workspace, 'openTextDocument').callsFake(async uri => {
            openedUris.push(uri as vscode.Uri);
            return { uri } as vscode.TextDocument;
        });
        sandbox.stub(vscode.window, 'showTextDocument').resolves(undefined as any);

        await provider.showResourceCommandOutput('api', 'migrate', 'first', '/repo/a/b/AppHost.csproj');
        await provider.showResourceCommandOutput('api', 'migrate', 'second', '/repo/a_b/AppHost.csproj');

        assert.strictEqual(openedUris.length, 2);
        assert.notStrictEqual(openedUris[0].toString(), openedUris[1].toString());
        assert.strictEqual(openedUris[0].path, 'api-migrate-output.txt');
        assert.strictEqual(openedUris[1].path, 'api-migrate-output.txt');
        assert.ok(!openedUris[0].path.includes('/repo/a/b'));
        assert.ok(!openedUris[1].path.includes('/repo/a_b'));
        assert.strictEqual(provider.provideTextDocumentContent(openedUris[0]), 'first');
        assert.strictEqual(provider.provideTextDocumentContent(openedUris[1]), 'second');
        provider.dispose();
    });
});

suite('AppHost tree actions', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    const appHostPath = '/repo/AppHost/AppHost.csproj';

    interface GatingHarness {
        readonly provider: AspireAppHostTreeProvider;
        readonly repository: AppHostDataRepository;
        readonly launchService: AppHostLaunchService;
        readonly configInfoProviderInstance: configInfoProvider.ConfigInfoProvider;
        readonly resolveCliPath: sinon.SinonStub;
        readonly checkCliAvailable: sinon.SinonStub;
        readonly getConfigInfo: sinon.SinonStub;
        readonly runCliCommand: sinon.SinonStub;
        readonly launch: sinon.SinonStub;
        readonly fireOperationChange: () => void;
        readonly setOperation: (operation: AppHostOperationState | undefined) => void;
        readonly isOperationSubscriptionDisposed: () => boolean;
        readonly fireCliPathConfigurationChange: () => void;
        readonly fireCliPathResolverChange: () => void;
        readonly fireWorkspaceFoldersChange: () => void;
        readonly areCliInvalidationSubscriptionsDisposed: () => boolean;
        dispose(): void;
    }

    function makeGatingRepository(overrides: Partial<Record<string, unknown>> = {}): AppHostDataRepository {
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        return {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: appHostPath,
            workspaceAppHostCandidatePaths: [appHostPath],
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: undefined,
            onDidChangeData,
            ...overrides,
        } as unknown as AppHostDataRepository;
    }

    /**
    * Builds a provider whose CLI resolution, pipeline protocol support, and durable operation
    * state are controlled by the test without touching a real Aspire CLI.
     */
    function makeGatingHarness(options?: {
        repository?: AppHostDataRepository;
        cliPath?: string;
        cliAvailable?: boolean;
        forceRefreshConfigInfo?: (callOptions?: configInfoProvider.ConfigInfoOptions) => ConfigInfo | null | Promise<ConfigInfo | null>;
    }): GatingHarness {
        const repository = options?.repository ?? makeGatingRepository();
        const operationEmitter = new vscode.EventEmitter<void>();
        let operation: AppHostOperationState | undefined;
        // Wrapping the event exposes whether the provider released its subscription on dispose;
        // the tree's own emitter goes quiet either way, so the refresh count cannot prove it.
        let operationSubscriptionDisposed = false;
        const onDidChangeOperationState: vscode.Event<void> = listener => {
            const subscription = operationEmitter.event(listener);
            return {
                dispose: () => {
                    operationSubscriptionDisposed = true;
                    subscription.dispose();
                },
            };
        };
        const launchService = {
            launch: sandbox.stub().resolves(),
            isLaunching: () => false,
            launchingPaths: [],
            clearLaunchingForRunningAppHost: () => { },
            onDidChangeLaunchingState: () => ({ dispose: () => { } }),
            onDidChangeOperationState,
            getActiveOperation: () => operation,
        } as unknown as AppHostLaunchService;
        const configurationEmitter = new vscode.EventEmitter<vscode.ConfigurationChangeEvent>();
        sandbox.stub(vscode.workspace, 'onDidChangeConfiguration').callsFake(listener =>
            configurationEmitter.event(listener as (event: vscode.ConfigurationChangeEvent) => unknown));
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        let workspaceFoldersSubscriptionDisposed = false;
        sandbox.stub(vscode.workspace, 'onDidChangeWorkspaceFolders').callsFake(listener => {
            const subscription = workspaceFoldersEmitter.event(listener as (event: vscode.WorkspaceFoldersChangeEvent) => unknown);
            return {
                dispose: () => {
                    workspaceFoldersSubscriptionDisposed = true;
                    subscription.dispose();
                },
            };
        });
        const cliPathResolverEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        let cliPathResolverSubscriptionDisposed = false;
        sandbox.stub(cliPathModule.cliPathResolver, 'onDidChangeForwarding').callsFake(listener => {
            const subscription = cliPathResolverEmitter.event(listener);
            return {
                dispose: () => {
                    cliPathResolverSubscriptionDisposed = true;
                    subscription.dispose();
                },
            };
        });
        const resolveCliPath = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
            cliPath: options?.cliPath ?? '/repo/tools/aspire',
            available: options?.cliAvailable ?? true,
            source: 'configured',
        });
        const checkCliAvailableOrRedirect = workspaceModule.checkCliAvailableOrRedirect;
        const checkCliAvailable = sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (operation, target, checkOptions) => checkOptions?.pinnedCliPath
                ? {
                    cliPath: checkOptions.pinnedCliPath,
                    available: options?.cliAvailable ?? true,
                }
                : await checkCliAvailableOrRedirect(operation, target, checkOptions));
        const configInfoProviderInstance = new configInfoProvider.ConfigInfoProvider(makeTerminalProvider());
        const getConfigInfo = sandbox.stub(configInfoProviderInstance, 'getConfigInfo').callsFake(
            async (callOptions?: configInfoProvider.ConfigInfoOptions) =>
                options?.forceRefreshConfigInfo
                    ? options.forceRefreshConfigInfo(callOptions)
                    : {
                    localSettingsPath: '/repo/aspire.config.json',
                    globalSettingsPath: '/repo/global-aspire.config.json',
                    availableFeatures: [],
                    localSettingsSchema: { properties: [] },
                    globalSettingsSchema: { properties: [] },
                    capabilities: [pipelineInteractionCapability],
                });
        const runCliCommand = sandbox.stub().resolves({ stdout: '[]', stderr: '' });
        const cliRunner = {
            withNoLogo: (args: string[]) => [...args, '--nologo'],
            runCliCommand,
            dispose: () => { },
        } as unknown as AppHostCliRunner;
        const provider = new AspireAppHostTreeProvider(
            repository,
            makeTerminalProvider(),
            launchService,
            undefined,
            makeClipboard(),
            configInfoProviderInstance,
            cliRunner);

        return {
            provider,
            repository,
            launchService,
            configInfoProviderInstance,
            resolveCliPath,
            checkCliAvailable,
            getConfigInfo,
            runCliCommand,
            launch: launchService.launch as unknown as sinon.SinonStub,
            fireOperationChange: () => operationEmitter.fire(),
            setOperation: value => { operation = value; },
            isOperationSubscriptionDisposed: () => operationSubscriptionDisposed,
            fireCliPathConfigurationChange: () => configurationEmitter.fire({
                affectsConfiguration: section => section === 'aspire.aspireCliExecutablePath',
            }),
            fireCliPathResolverChange: () => cliPathResolverEmitter.fire(windowCliPathTarget),
            fireWorkspaceFoldersChange: () => workspaceFoldersEmitter.fire({ added: [], removed: [] }),
            areCliInvalidationSubscriptionsDisposed: () =>
                cliPathResolverSubscriptionDisposed && workspaceFoldersSubscriptionDisposed,
            dispose: () => {
                provider.dispose();
                operationEmitter.dispose();
                configurationEmitter.dispose();
                cliPathResolverEmitter.dispose();
                workspaceFoldersEmitter.dispose();
            },
        };
    }

    /** Renders the tree until the AppHost's CLI has resolved. */
    async function renderUntilProbed(harness: GatingHarness, expectedContextValue: string): Promise<vscode.TreeItem> {
        let item = harness.provider.getChildren()[0];
        await waitForCondition(
            () => {
                item = harness.provider.getChildren()[0];
                return item.contextValue === expectedContextValue;
            },
            `Expected the AppHost row to render "${expectedContextValue}", last saw "${item.contextValue}".`);

        return item;
    }

    test('baseline actions appear after CLI resolution without command capabilities', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [],
            }),
        });

        // The first synchronous render cannot know whether the AppHost CLI is available.
        const initial = harness.provider.getChildren()[0];
        assert.strictEqual(initial.contextValue, 'workspaceAppHost');
        assert.deepStrictEqual(harness.provider.getChildren(initial).map(item => item.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostPath',
        ]);

        const probed = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        assert.deepStrictEqual(harness.provider.getChildren(probed).map(item => item.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostAction:deploy',
            'workspaceAppHostAction:publish',
            'workspaceAppHostAction:runPipelineStep',
            'workspaceAppHostAction:debugPipelineStep',
            'workspaceAppHostPath',
        ]);
        assert.strictEqual(harness.getConfigInfo.called, false);
        harness.dispose();
    });

    test('an unavailable CLI hides every action without interrupting the user while rendering', async () => {
        const showErrorMessage = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const harness = makeGatingHarness({ cliAvailable: false });

        const item = harness.provider.getChildren()[0];
        await waitForCondition(() => harness.resolveCliPath.callCount === 1, 'Expected the CLI to be resolved once.');
        // The failed probe is cached, so re-rendering neither re-resolves nor grants actions.
        harness.provider.getChildren();
        harness.provider.getChildren();

        assert.strictEqual(harness.provider.getChildren()[0].contextValue, 'workspaceAppHost');
        assert.deepStrictEqual(harness.provider.getChildren(item).map(item => item.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostPath',
        ]);
        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        // Drawing the tree must not nag about a CLI the user has not asked to use yet.
        assert.strictEqual(showErrorMessage.called, false);

        // An explicit action does report it, through the shared availability gate.
        await assert.rejects(harness.provider.deployAppHost(item as WorkspaceAppHostItem), vscode.CancellationError);
        assert.strictEqual(showErrorMessage.callCount, 1);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('changing the configured CLI re-resolves the AppHost actions', async () => {
        let currentCliPath = '/repo/tools/aspire';
        const harness = makeGatingHarness();
        harness.resolveCliPath.callsFake(async () => ({ cliPath: currentCliPath, available: true, source: 'configured' as const }));
        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        currentCliPath = '/repo/tools/other-aspire';
        harness.fireCliPathConfigurationChange();

        const reprobed = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        assert.strictEqual(reprobed.contextValue, 'workspaceAppHost:canDeploy:canPublish:canDo');
        assert.strictEqual(harness.resolveCliPath.callCount, 2);
        harness.dispose();
    });

    test('a canonical CLI resolver change re-resolves cached AppHost actions', async () => {
        let currentCliPath = '/repo/tools/aspire';
        const harness = makeGatingHarness();
        harness.resolveCliPath.callsFake(async () => ({
            cliPath: currentCliPath,
            available: true,
            source: 'configured' as const,
        }));
        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        currentCliPath = '/repo/tools/resolved-aspire';
        harness.fireCliPathResolverChange();

        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        assert.strictEqual(harness.resolveCliPath.callCount, 2);
        harness.dispose();
    });

    test('CLI resolver and workspace-folder invalidation subscriptions are disposed', () => {
        const harness = makeGatingHarness();

        assert.strictEqual(harness.areCliInvalidationSubscriptionsDisposed(), false);
        harness.provider.dispose();

        assert.strictEqual(harness.areCliInvalidationSubscriptionsDisposed(), true);
        harness.dispose();
    });

    test('a configured CLI change during resolution cannot restore a stale CLI', async () => {
        let completeOldResolution!: (result: cliPathModule.CliPathResolutionResult) => void;
        const oldResolution = new Promise<cliPathModule.CliPathResolutionResult>(resolve => completeOldResolution = resolve);
        const harness = makeGatingHarness();
        harness.resolveCliPath.onFirstCall().returns(oldResolution);
        harness.resolveCliPath.onSecondCall().resolves({
            cliPath: '/repo/tools/new-aspire',
            available: true,
            source: 'configured',
        });

        harness.provider.getChildren();
        await waitForCondition(() => harness.resolveCliPath.callCount === 1, 'Expected the old CLI probe to start.');
        harness.fireCliPathConfigurationChange();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        completeOldResolution({
            cliPath: '/repo/tools/old-aspire',
            available: true,
            source: 'configured',
        });
        await flushPromises();
        await harness.provider.deployAppHost(item as WorkspaceAppHostItem);

        assert.deepStrictEqual(harness.checkCliAvailable.getCalls().map(call => call.args), [
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/new-aspire' }],
        ]);
        harness.dispose();
    });

    test('a workspace-folder change during resolution cannot restore a stale CLI', async () => {
        let completeOldResolution!: (result: cliPathModule.CliPathResolutionResult) => void;
        const oldResolution = new Promise<cliPathModule.CliPathResolutionResult>(resolve => completeOldResolution = resolve);
        const harness = makeGatingHarness();
        harness.resolveCliPath.onFirstCall().returns(oldResolution);
        harness.resolveCliPath.onSecondCall().resolves({
            cliPath: '/repo/tools/new-aspire',
            available: true,
            source: 'configured',
        });

        harness.provider.getChildren();
        await waitForCondition(() => harness.resolveCliPath.callCount === 1, 'Expected the old CLI probe to start.');
        harness.fireWorkspaceFoldersChange();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        completeOldResolution({
            cliPath: '/repo/tools/old-aspire',
            available: true,
            source: 'configured',
        });
        await flushPromises();
        await harness.provider.publishAppHost(item as WorkspaceAppHostItem);

        assert.deepStrictEqual(harness.checkCliAvailable.getCalls().map(call => call.args), [
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/new-aspire' }],
        ]);
        harness.dispose();
    });

    test('repeated renders reuse one CLI resolution per AppHost identity', async () => {
        const harness = makeGatingHarness();

        harness.provider.getChildren();
        harness.provider.getChildren();
        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.provider.getChildren();
        harness.provider.getChildren();

        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        harness.dispose();
    });

    test('render cache lookups do not perform filesystem AppHost identity discovery', async () => {
        const identityDiscovery = sandbox.stub(appHostIdentityModule, 'getAppHostIdentityKey').throws(
            new Error('Rendering must not discover filesystem identity.'));
        const harness = makeGatingHarness();

        harness.provider.getChildren();
        harness.provider.getChildren();
        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.provider.getChildren();

        assert.strictEqual(identityDiscovery.called, false);
        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        harness.dispose();
    });

    test('each AppHost resolves its own CLI', async () => {
        const primaryPath = '/repo/primary/AppHost/AppHost.csproj';
        const secondaryPath = '/repo/secondary/AppHost/AppHost.csproj';
        const primaryFolder = createWorkspaceFolder('primary', '/repo/primary');
        const secondaryFolder = createWorkspaceFolder('secondary', '/repo/secondary');
        const primaryTarget = workspaceFolderCliPathTarget(primaryFolder);
        const secondaryTarget = workspaceFolderCliPathTarget(secondaryFolder);
        const primaryCliPath = '/repo/primary/tools/aspire';
        const secondaryCliPath = '/repo/secondary/tools/aspire';
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake(uri =>
            uri.path.startsWith(`${secondaryFolder.uri.path}/`) ? secondaryFolder : primaryFolder);
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                workspaceAppHostPath: primaryPath,
                workspaceAppHostCandidatePaths: [primaryPath, secondaryPath],
                workspaceAppHostName: undefined,
            }),
        });
        harness.resolveCliPath.callsFake(async (target: CliPathResolutionTarget) => ({
            cliPath: target.kind === 'workspaceFolder' && target.workspaceFolder.name === 'secondary' ? secondaryCliPath : primaryCliPath,
            available: true,
            source: 'configured' as const,
        }));

        harness.provider.getChildren();
        await waitForCondition(
            () => {
                const [group] = harness.provider.getChildren();
                return harness.provider.getChildren(group).every(item => item.contextValue !== 'workspaceAppHost');
            },
            'Expected both AppHosts to resolve their own CLI.');

        const [group] = harness.provider.getChildren();
        assert.deepStrictEqual(harness.provider.getChildren(group).map(item => item.contextValue), [
            'workspaceAppHost:canDeploy:canPublish:canDo',
            'workspaceAppHost:canDeploy:canPublish:canDo',
        ]);
        assert.deepStrictEqual(harness.resolveCliPath.getCalls().map(call => call.args), [
            [primaryTarget],
            [secondaryTarget],
        ]);
        harness.dispose();
    });

    test('actions reuse the resolved CLI pair instead of resolving again', async () => {
        const harness = makeGatingHarness();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.deployAppHost(item as WorkspaceAppHostItem);
        await harness.provider.publishAppHost(item as WorkspaceAppHostItem);
        await harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'deploy', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
            [appHostPath, 'publish', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
            [appHostPath, 'do', true, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('a cached CLI deleted before launch fails closed without resolving a replacement', async () => {
        const harness = makeGatingHarness();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.checkCliAvailable.resolves({ cliPath: '/repo/tools/aspire', available: false });

        await assert.rejects(harness.provider.deployAppHost(item as WorkspaceAppHostItem), vscode.CancellationError);

        assert.deepStrictEqual(harness.checkCliAvailable.getCalls().map(call => call.args), [
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/aspire' }],
        ]);
        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        assert.strictEqual(harness.launch.called, false);
        assert.strictEqual(harness.provider.getChildren()[0].contextValue, 'workspaceAppHost');
        harness.dispose();
    });

    test('actions requested before CLI resolution wait for and reuse it', async () => {
        const harness = makeGatingHarness();
        const item = new WorkspaceAppHostItem(appHostPath);

        // Nothing has rendered yet, so the first action resolves the owning CLI on demand.
        await harness.provider.deployAppHost(item);
        await harness.provider.publishAppHost(item);

        assert.strictEqual(harness.resolveCliPath.callCount, 1);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'deploy', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
            [appHostPath, 'publish', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('an older same-action CLI validation cannot launch after a newer invocation', async () => {
        let completeOlderValidation!: (result: { cliPath: string; available: boolean }) => void;
        const olderValidation = new Promise<{ cliPath: string; available: boolean }>(
            resolve => completeOlderValidation = resolve);
        const harness = makeGatingHarness();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.checkCliAvailable.onFirstCall().returns(olderValidation);

        const olderInvocation = harness.provider.deployAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(
            () => harness.checkCliAvailable.callCount === 1,
            'Expected the older deploy CLI validation to start.');
        await harness.provider.deployAppHost(item as WorkspaceAppHostItem);
        completeOlderValidation({ cliPath: '/repo/tools/aspire', available: true });
        await assert.rejects(olderInvocation, vscode.CancellationError);

        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'deploy', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('an older cross-action CLI validation cannot launch after a newer invocation', async () => {
        let completeOlderValidation!: (result: { cliPath: string; available: boolean }) => void;
        const olderValidation = new Promise<{ cliPath: string; available: boolean }>(
            resolve => completeOlderValidation = resolve);
        const harness = makeGatingHarness();
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.checkCliAvailable.onFirstCall().returns(olderValidation);

        const olderDeploy = harness.provider.deployAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(
            () => harness.checkCliAvailable.callCount === 1,
            'Expected the older deploy CLI validation to start.');
        await harness.provider.publishAppHost(item as WorkspaceAppHostItem);
        completeOlderValidation({ cliPath: '/repo/tools/aspire', available: true });
        await assert.rejects(olderDeploy, vscode.CancellationError);

        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'publish', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('pipeline step resolution reuses the injected provider and the resolved CLI pair', async () => {
        const harness = makeGatingHarness();
        const ownedGetConfigInfo = sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves(null);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.debugPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.deepStrictEqual(harness.getConfigInfo.getCalls().map(call => call.args), [
            [{
                target: windowCliPathTarget,
                cliPath: '/repo/tools/aspire',
                suppressErrors: true,
                forceRefresh: true,
            }],
            [{
                target: windowCliPathTarget,
                cliPath: '/repo/tools/aspire',
                suppressErrors: true,
                forceRefresh: true,
            }],
        ]);
        // A provider constructed by the tree instead of the injected one would answer here.
        assert.strictEqual(ownedGetConfigInfo.called, false);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'do', false, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('pipeline interaction support comes from the forced action snapshot', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [pipelineInteractionCapability],
            }),
        });
        const hasCapability = sandbox.stub(harness.configInfoProviderInstance, 'hasCapability').rejects(
            new Error('Pipeline support should come from the forced config-info snapshot.'));
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').resolves('deploy');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.strictEqual(hasCapability.called, false);
        assert.strictEqual(showInputBox.called, false);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'do', true, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('structured pipeline step capability lists before launching the selected step', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [pipelineInteractionCapability, pipelineStepListJsonCapability],
            }),
        });
        harness.runCliCommand.resolves({
            stdout: '[{"name":"deploy","description":"Deploy the app","dependsOn":["publish"],"tags":[]}]',
            stderr: '',
        });
        const showQuickPick = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items =>
            (items as readonly vscode.QuickPickItem[])[0]);
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.strictEqual(showQuickPick.callCount, 1);
        assert.strictEqual(showInputBox.called, false);
        assert.strictEqual(harness.runCliCommand.callCount, 1);
        assert.strictEqual(harness.runCliCommand.firstCall.args[0], 'list pipeline steps');
        assert.deepStrictEqual(harness.runCliCommand.firstCall.args[1],
            ['do', '--list-steps', '--format', 'json', '--apphost', appHostPath, '--nologo']);
        assert.strictEqual(harness.runCliCommand.firstCall.args[2].target, windowCliPathTarget);
        assert.strictEqual(harness.runCliCommand.firstCall.args[2].cliPath, '/repo/tools/aspire');
        assert.strictEqual(harness.runCliCommand.firstCall.args[2].timeoutMs, null);
        assert.ok(harness.runCliCommand.firstCall.args[2].cancellationToken);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'do', true, 'deploy', windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('structured pipeline discovery disables only the selected action while loading', async () => {
        const configInfo: ConfigInfo = {
            localSettingsPath: '/repo/aspire.config.json',
            globalSettingsPath: '/repo/global-aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [pipelineInteractionCapability, pipelineStepListJsonCapability],
        };
        let deferCapabilityRefresh = false;
        let resolveCapabilityRefresh!: (value: ConfigInfo) => void;
        const capabilityRefresh = new Promise<ConfigInfo>(resolve => { resolveCapabilityRefresh = resolve; });
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => deferCapabilityRefresh ? capabilityRefresh : configInfo,
        });
        let resolveList!: (value: { stdout: string; stderr: string }) => void;
        harness.runCliCommand.returns(new Promise(resolve => { resolveList = resolve; }));
        sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items =>
            (items as readonly vscode.QuickPickItem[])[0]);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        const initialConfigInfoCallCount = harness.getConfigInfo.callCount;
        deferCapabilityRefresh = true;

        const action = harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(
            () => harness.getConfigInfo.callCount > initialConfigInfoCallCount,
            'Expected pipeline capability refresh to start.');

        const loadingItem = harness.provider.getChildren()[0];
        assert.strictEqual(loadingItem.contextValue, 'workspaceAppHost:canDeploy:canPublish:canDo');
        const loadingChildren = harness.provider.getChildren(loadingItem);
        assert.deepStrictEqual(loadingChildren.map(child => child.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostAction:deploy',
            'workspaceAppHostAction:publish',
            'workspaceAppHostAction:runPipelineStep:loading',
            'workspaceAppHostAction:debugPipelineStep',
            'workspaceAppHostPath',
        ]);
        const loadingAction = loadingChildren[5];
        assert.strictEqual(loadingAction.description, loadingPipelineSteps);
        assert.strictEqual(loadingAction.command, undefined);
        assert.strictEqual(loadingChildren[6].command?.command, 'aspire-vscode.debugPipelineStepAppHost');

        resolveCapabilityRefresh(configInfo);
        await waitForCondition(() => harness.runCliCommand.calledOnce, 'Expected pipeline discovery to start.');
        resolveList({
            stdout: '[{"name":"deploy","dependsOn":[],"tags":[]}]',
            stderr: '',
        });
        await action;

        const readyItem = harness.provider.getChildren()[0];
        assert.strictEqual(readyItem.contextValue, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.dispose();
    });

    test('structured pipeline step incompatibility falls back to CLI interaction', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [pipelineInteractionCapability, pipelineStepListJsonCapability],
            }),
        });
        harness.runCliCommand.rejects(new AspireCliFailedError('list pipeline steps', 9, '', 'AppHost is incompatible'));
        const showQuickPick = sandbox.stub(vscode.window, 'showQuickPick');
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.strictEqual(showQuickPick.called, false);
        assert.strictEqual(showInputBox.called, false);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'do', true, undefined, windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('structured pipeline step failure other than incompatibility does not fall back', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [pipelineInteractionCapability, pipelineStepListJsonCapability],
            }),
        });
        const failure = new AspireCliFailedError('list pipeline steps', 6, '', 'AppHost failed');
        harness.runCliCommand.rejects(failure);
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await assert.rejects(harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem), error => error === failure);

        assert.strictEqual(showInputBox.called, false);
        assert.strictEqual(harness.launch.called, false);
        assert.strictEqual(
            harness.provider.getChildren(harness.provider.getChildren()[0])[5].contextValue,
            'workspaceAppHostAction:runPipelineStep');
        harness.dispose();
    });

    test('a pipeline action invalidated while choosing a legacy step does not launch', async () => {
        let completeInput!: (value: string | undefined) => void;
        const input = new Promise<string | undefined>(resolve => completeInput = resolve);
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [],
            }),
        });
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').returns(input);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        const action = harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(() => showInputBox.called, 'Expected the legacy pipeline prompt to open.');
        harness.fireCliPathResolverChange();
        completeInput('deploy');

        await assert.rejects(action, vscode.CancellationError);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('a pinned CLI deleted while choosing a legacy step does not launch', async () => {
        let completeInput!: (value: string | undefined) => void;
        const input = new Promise<string | undefined>(resolve => completeInput = resolve);
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [],
            }),
        });
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').returns(input);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        const action = harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(() => showInputBox.called, 'Expected the legacy pipeline prompt to open.');
        harness.checkCliAvailable.onSecondCall().resolves({
            cliPath: '/repo/tools/aspire',
            available: false,
        });
        completeInput('deploy');

        await assert.rejects(action, vscode.CancellationError);
        assert.deepStrictEqual(harness.checkCliAvailable.getCalls().map(call => call.args), [
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/aspire' }],
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/aspire' }],
        ]);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('a legacy CLI replaced by a pipeline-interaction CLI while choosing a step does not launch', async () => {
        let configReadCount = 0;
        let completeInput!: (value: string | undefined) => void;
        const input = new Promise<string | undefined>(resolve => completeInput = resolve);
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: configReadCount++ === 0 ? [] : [pipelineInteractionCapability],
            }),
        });
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').returns(input);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        const action = harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(() => showInputBox.called, 'Expected the legacy pipeline prompt to open.');
        completeInput('deploy');

        await assert.rejects(action, vscode.CancellationError);
        assert.strictEqual(harness.getConfigInfo.callCount, 2);
        assert.strictEqual(harness.launch.called, false);
        assert.strictEqual(
            harness.provider.getChildren(harness.provider.getChildren()[0])[5].contextValue,
            'workspaceAppHostAction:runPipelineStep');
        harness.dispose();
    });

    test('a pipeline-interaction CLI replaced by a legacy CLI does not launch without a selected step', async () => {
        let configReadCount = 0;
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: configReadCount++ === 0 ? [pipelineInteractionCapability] : [],
            }),
        });
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').resolves('deploy');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await assert.rejects(
            harness.provider.debugPipelineStepAppHost(item as WorkspaceAppHostItem),
            vscode.CancellationError);

        assert.strictEqual(showInputBox.called, false);
        assert.strictEqual(harness.getConfigInfo.callCount, 2);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('a structured-list CLI replaced while its picker is open does not launch', async () => {
        let configReadCount = 0;
        let completeQuickPick!: (item: vscode.QuickPickItem | undefined) => void;
        const quickPick = new Promise<vscode.QuickPickItem | undefined>(resolve => completeQuickPick = resolve);
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: configReadCount++ === 0
                    ? [pipelineInteractionCapability, pipelineStepListJsonCapability]
                    : [pipelineInteractionCapability],
            }),
        });
        harness.runCliCommand.resolves({
            stdout: '[{"name":"deploy","dependsOn":[],"tags":[]}]',
            stderr: '',
        });
        const showQuickPick = sandbox.stub(vscode.window, 'showQuickPick').returns(quickPick);
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        const action = harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);
        await waitForCondition(() => showQuickPick.called, 'Expected the structured pipeline picker to open.');
        completeQuickPick((showQuickPick.firstCall.args[0] as readonly vscode.QuickPickItem[])[0]);

        await assert.rejects(action, vscode.CancellationError);
        assert.strictEqual(harness.getConfigInfo.callCount, 2);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('a pipeline-interaction CLI replaced by structured listing does not launch', async () => {
        let configReadCount = 0;
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: configReadCount++ === 0
                    ? [pipelineInteractionCapability]
                    : [pipelineInteractionCapability, pipelineStepListJsonCapability],
            }),
        });
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await assert.rejects(
            harness.provider.debugPipelineStepAppHost(item as WorkspaceAppHostItem),
            vscode.CancellationError);

        assert.strictEqual(harness.runCliCommand.called, false);
        assert.strictEqual(harness.getConfigInfo.callCount, 2);
        assert.strictEqual(harness.launch.called, false);
        harness.dispose();
    });

    test('a legacy CLI still prompts for the pipeline step locally', async () => {
        const harness = makeGatingHarness({
            forceRefreshConfigInfo: () => ({
                localSettingsPath: '/repo/aspire.config.json',
                globalSettingsPath: '/repo/global-aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [],
            }),
        });
        const showInputBox = sandbox.stub(vscode.window, 'showInputBox').resolves(' migrate ');
        const item = await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');

        await harness.provider.runPipelineStepAppHost(item as WorkspaceAppHostItem);

        assert.strictEqual(showInputBox.callCount, 1);
        assert.strictEqual(harness.getConfigInfo.callCount, 2);
        assert.deepStrictEqual(harness.checkCliAvailable.getCalls().map(call => call.args), [
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/aspire' }],
            ['debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/tools/aspire' }],
        ]);
        assert.deepStrictEqual(harness.launch.getCalls().map(call => call.args), [
            [appHostPath, 'do', true, 'migrate', windowCliPathTarget, '/repo/tools/aspire'],
        ]);
        harness.dispose();
    });

    test('deploy, publish and pipeline handlers report a missing AppHost instead of throwing', async () => {
        const harness = makeGatingHarness();
        const showWarningMessage = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        const pathlessItem = new WorkspaceResourcesItem([], null, undefined, undefined);

        await harness.provider.deployAppHost(undefined);
        await harness.provider.publishAppHost(undefined);
        await harness.provider.runPipelineStepAppHost(undefined);
        await harness.provider.debugPipelineStepAppHost(undefined);
        await harness.provider.deployAppHost(pathlessItem);
        await harness.provider.deployAppHost({} as WorkspaceAppHostItem);

        assert.deepStrictEqual(showWarningMessage.getCalls().map(call => call.args), Array(6).fill([appHostSourceNotFound]));
        assert.strictEqual(harness.launch.called, false);
        assert.strictEqual(harness.resolveCliPath.called, false);
        harness.dispose();
    });

    test('a running workspace AppHost row carries all baseline actions', async () => {
        const runningAppHost = makeAppHost({ appHostPath, resources: [] });
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                appHosts: [runningAppHost],
                workspaceAppHost: runningAppHost,
            }),
        });

        const item = await renderUntilProbed(harness, 'workspaceResources:hasAppHost:canDeploy:canPublish:canDo');

        assert.ok(item instanceof WorkspaceResourcesItem);
        harness.dispose();
    });

    test('a global AppHost row carries all baseline actions', async () => {
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                viewMode: 'global' as ViewMode,
                appHosts: [makeAppHost({ appHostPath })],
                workspaceAppHostPath: undefined,
                workspaceAppHostCandidatePaths: [],
            }),
        });

        const item = await renderUntilProbed(harness, 'appHost:canDeploy:canPublish:canDo');

        assert.ok(item instanceof AppHostItem);
        harness.dispose();
    });

    test('a workspace row without a running AppHost carries no action tokens', async () => {
        // Resources can arrive before `aspire ps` reports the AppHost. Actions require the running
        // AppHost context, so the row stays plain after CLI resolution.
        const harness = makeGatingHarness({
            repository: makeGatingRepository({ workspaceResources: [makeResource()] }),
        });

        harness.provider.getChildren();
    await waitForCondition(() => harness.resolveCliPath.callCount === 1, 'Expected the AppHost CLI to resolve.');

        const item = harness.provider.getChildren()[0];
        assert.ok(item instanceof WorkspaceResourcesItem);
        assert.strictEqual(item.contextValue, 'workspaceResources');
        harness.dispose();
    });

    test('a workspace AppHost renders its durable operation instead of launch actions', async () => {
        const harness = makeGatingHarness();
        await renderUntilProbed(harness, 'workspaceAppHost:canDeploy:canPublish:canDo');
        harness.setOperation({ appHostPath, command: 'deploy', noDebug: false });

        const item = harness.provider.getChildren()[0];

        assert.strictEqual(item.contextValue, 'workspaceAppHostOperating');
        assert.strictEqual(item.description, 'Deploying...');
        assert.deepStrictEqual(item.iconPath, new vscode.ThemeIcon('loading~spin'));
        // Only the source and path affordances survive an in-flight operation.
        assert.deepStrictEqual(harness.provider.getChildren(item).map(child => child.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostPath',
        ]);
        harness.dispose();
    });

    test('operation descriptions cover deploy, publish and both pipeline step modes', async () => {
        const harness = makeGatingHarness();

        const descriptions: (string | boolean | undefined)[] = [];
        for (const operation of [
            { appHostPath, command: 'deploy', noDebug: false },
            { appHostPath, command: 'publish', noDebug: false },
            { appHostPath, command: 'do', noDebug: true },
            { appHostPath, command: 'do', noDebug: false },
        ] satisfies AppHostOperationState[]) {
            harness.setOperation(operation);
            descriptions.push(harness.provider.getChildren()[0].description);
        }

        assert.deepStrictEqual(descriptions, [
            'Deploying...',
            'Publishing...',
            'Running pipeline step...',
            'Debugging pipeline step...',
        ]);
        harness.dispose();
    });

    test('a running workspace AppHost renders its durable operation', async () => {
        const runningAppHost = makeAppHost({ appHostPath, resources: [] });
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                appHosts: [runningAppHost],
                workspaceAppHost: runningAppHost,
            }),
        });
        harness.setOperation({ appHostPath, command: 'publish', noDebug: false });

        const item = harness.provider.getChildren()[0];

        assert.ok(item instanceof WorkspaceResourcesItem);
        assert.strictEqual(item.contextValue, 'workspaceResources:hasAppHost:operating');
        assert.strictEqual(item.description, 'Publishing...');
        assert.deepStrictEqual(item.iconPath, new vscode.ThemeIcon('loading~spin'));
        harness.dispose();
    });

    test('a global AppHost renders its durable operation', async () => {
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                viewMode: 'global' as ViewMode,
                appHosts: [makeAppHost({ appHostPath })],
                workspaceAppHostPath: undefined,
                workspaceAppHostCandidatePaths: [],
            }),
        });
        harness.setOperation({ appHostPath, command: 'do', noDebug: true });

        const item = harness.provider.getChildren()[0];

        assert.ok(item instanceof AppHostItem);
        assert.strictEqual(item.contextValue, 'appHost:operating');
        assert.strictEqual(item.description, 'Running pipeline step...');
        assert.deepStrictEqual(item.iconPath, new vscode.ThemeIcon('loading~spin'));
        harness.dispose();
    });

    test('a stopping AppHost keeps its stopping state while an operation runs', async () => {
        const runningAppHost = makeAppHost({ appHostPath, resources: [] });
        const harness = makeGatingHarness({
            repository: makeGatingRepository({
                viewMode: 'global' as ViewMode,
                appHosts: [runningAppHost],
                workspaceAppHostPath: undefined,
                workspaceAppHostCandidatePaths: [],
            }),
        });
        harness.setOperation({ appHostPath, command: 'deploy', noDebug: false });
        harness.provider.notifyAppHostStopping(appHostPath);

        const item = harness.provider.getChildren()[0];

        assert.strictEqual(item.contextValue, 'appHost:stopping');
        assert.strictEqual(item.description, 'Stopping...');
        harness.dispose();
    });

    test('operation state changes refresh the tree until the provider is disposed', async () => {
        const harness = makeGatingHarness();
        let refreshCount = 0;
        const subscription = harness.provider.onDidChangeTreeData(() => { refreshCount++; });

        harness.fireOperationChange();
        harness.fireOperationChange();
        const refreshesWhileSubscribed = refreshCount;
        assert.strictEqual(harness.isOperationSubscriptionDisposed(), false);
        harness.provider.dispose();
        harness.fireOperationChange();

        assert.strictEqual(refreshesWhileSubscribed, 2);
        assert.strictEqual(refreshCount, 2);
        assert.strictEqual(harness.isOperationSubscriptionDisposed(), true);
        subscription.dispose();
        harness.dispose();
    });

    test('context menu when clauses follow the rendered context values', () => {
        const manifest = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../package.json'), 'utf8')) as {
            contributes: { menus: { 'view/item/context': { command: string; when: string }[] } };
        };
        const whenClauseFor = (command: string): RegExp => {
            const entry = manifest.contributes.menus['view/item/context'].find(item => item.command === `aspire-vscode.${command}`);
            assert.ok(entry, `Expected a context menu entry for ${command}.`);
            const match = /viewItem =~ \/(.*)\/$/.exec(entry.when);
            assert.ok(match, `Expected ${command} to gate on a viewItem regex, found "${entry.when}".`);
            return new RegExp(match[1]);
        };
        const matchingContextValues = (command: string): string[] =>
            renderedContextValues.filter(contextValue => whenClauseFor(command).test(contextValue));

        // Every context value the tree can render for an AppHost row.
        const renderedContextValues = [
            'appHost',
            'appHost:canDeploy:canPublish:canDo',
            'appHost:operating',
            'appHost:stopping',
            'workspaceResources',
            'workspaceResources:hasAppHost',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:operating',
            'workspaceResources:operating',
            'workspaceResources:stopping',
            'workspaceAppHost',
            'workspaceAppHost:canDeploy:canPublish:canDo',
            'workspaceAppHostLaunching',
            'workspaceAppHostOperating',
            'workspaceAppHostStopping',
            'workspaceAppHostPath',
            'workspaceAppHostAction:deploy',
            'workspaceAppHostsGroup',
            'runningAppHostsGroup',
        ];

        // Rows with resolved baseline actions only, and never a row that is busy with an operation.
        assert.deepStrictEqual(matchingContextValues('deployAppHost'), [
            'appHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceAppHost:canDeploy:canPublish:canDo',
        ]);
        assert.deepStrictEqual(matchingContextValues('publishAppHost'), [
            'appHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceAppHost:canDeploy:canPublish:canDo',
        ]);
        for (const command of ['runPipelineStepAppHost', 'debugPipelineStepAppHost']) {
            assert.deepStrictEqual(matchingContextValues(command), [
                'appHost:canDeploy:canPublish:canDo',
                'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
                'workspaceAppHost:canDeploy:canPublish:canDo',
            ]);
        }

        // Run and Debug stay on idle workspace AppHosts and drop out while one is operating.
        for (const command of ['runAppHost', 'debugAppHost']) {
            assert.deepStrictEqual(matchingContextValues(command), [
                'workspaceAppHost',
                'workspaceAppHost:canDeploy:canPublish:canDo',
            ]);
        }

        // Source and path affordances survive every state, including an in-flight operation.
        for (const command of ['openAppHostSource', 'copyAppHostPath']) {
            assert.deepStrictEqual(matchingContextValues(command), [
                'appHost',
                'appHost:canDeploy:canPublish:canDo',
                'appHost:operating',
                'appHost:stopping',
                'workspaceResources',
                'workspaceResources:hasAppHost',
                'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
                'workspaceResources:hasAppHost:operating',
                'workspaceResources:operating',
                'workspaceResources:stopping',
                'workspaceAppHost',
                'workspaceAppHost:canDeploy:canPublish:canDo',
                'workspaceAppHostLaunching',
                'workspaceAppHostOperating',
                'workspaceAppHostStopping',
            ]);
        }
        assert.deepStrictEqual(matchingContextValues('viewAppHostSource'), [
            'appHost',
            'appHost:canDeploy:canPublish:canDo',
            'appHost:operating',
            'appHost:stopping',
            'workspaceResources:hasAppHost',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:operating',
            'workspaceResources:stopping',
        ]);

        // Stopping a running AppHost stays available while it deploys, publishes or runs a step.
        assert.deepStrictEqual(matchingContextValues('stopAppHost'), [
            'appHost',
            'appHost:canDeploy:canPublish:canDo',
            'appHost:operating',
            'workspaceResources:hasAppHost',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:operating',
        ]);
        assert.deepStrictEqual(matchingContextValues('openDashboard'), [
            'appHost',
            'appHost:canDeploy:canPublish:canDo',
            'appHost:operating',
            'appHost:stopping',
            'workspaceResources',
            'workspaceResources:hasAppHost',
            'workspaceResources:hasAppHost:canDeploy:canPublish:canDo',
            'workspaceResources:hasAppHost:operating',
            'workspaceResources:operating',
            'workspaceResources:stopping',
        ]);
    });
});
