import * as assert from 'assert';
import * as childProcess from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import * as cliPathModule from '../utils/cliPath';
import * as configInfoProvider from '../utils/configInfoProvider';
import { AppHostDataRepository, shortenPath, shortenPaths } from '../views/AppHostDataRepository';
import { AspireAppHostTreeProvider, getResourceContextValue, getResourceIcon, getResourceCommandIcon, resolveAppHostSourcePath, buildResourceDescription } from '../views/AspireAppHostTreeProvider';
import type { AppHostDisplayInfo, ResourceJson, ViewMode } from '../views/AppHostDataRepository';
import { ResourceCommandInputType } from '../views/AppHostDataRepository';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import type { AspireSubcommand } from '../utils/AspireTerminalProvider';
import { AspireTerminalProvider, shellArg } from '../utils/AspireTerminalProvider';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import { terminalCommandArgumentControlCharacters } from '../loc/strings';

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
    return new AppHostLaunchService();
}

function makeTerminalProvider(): AspireTerminalProvider {
    return {
        getAspireCliExecutablePath: async () => 'aspire',
        createEnvironment: () => ({}),
        sendAspireCommandToAspireTerminal: () => { },
    } as unknown as AspireTerminalProvider;
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
            fs.rmSync(directory, { recursive: true, force: true });
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
    sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: proof.cliPath, available: true, source: 'configured' });
    sandbox.stub(terminalProvider, 'isCliDebugLoggingEnabled').returns(false);
    sandbox.stub(terminalProvider, 'getAspireTerminal').returns({
        terminal: {
            shellIntegration: {
                executeCommand: (commandLine: string) => {
                    commandLines.push(commandLine);
                    return {} as vscode.TerminalShellExecution;
                }
            },
            sendText: () => assert.fail('expected shell integration to execute the command'),
            show: () => { }
        } as unknown as vscode.Terminal,
        dispose: () => { },
    });

    return {
        terminalProvider,
        dispose: () => {
            terminalProvider.dispose();
            subscriptions.forEach(subscription => subscription.dispose());
        },
    };
}

async function flushPromises(): Promise<void> {
    await new Promise(resolve => setImmediate(resolve));
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
        const commands: AspireSubcommand[] = [];
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
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'appHost:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual((stoppingItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.deepStrictEqual(commands, [['stop', '--apphost', shellArg(appHostPath)]]);
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

    test('stop AppHost requests refresh after terminal command is sent', async () => {
        const appHostPath = path.resolve('workspace', 'apps', 'Store', 'AppHost.csproj');
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const requestAppHostStopRefresh = sandbox.stub();
        let resolveTerminalCommand: (() => void) | undefined;
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
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: () => new Promise<void>(resolve => { resolveTerminalCommand = resolve; }),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const [item] = provider.getChildren();

        const stopTask = provider.stopAppHost(item as any);

        assert.strictEqual(requestAppHostStopRefresh.callCount, 0);
        resolveTerminalCommand?.();
        await stopTask;

        assert.strictEqual(requestAppHostStopRefresh.callCount, 1);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        provider.dispose();
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
        assert.deepStrictEqual(provider.stoppingPaths, [process.platform === 'win32' ? appHostPath.toLowerCase() : appHostPath]);
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

        const [groupItem] = provider.getChildren();
        const [candidateItem] = provider.getChildren(groupItem);
        assert.strictEqual(candidateItem.contextValue, 'workspaceAppHost');
        assert.notStrictEqual(candidateItem.description, 'Stopping...');
        assert.strictEqual(provider.stoppingPaths.length, 0);
        assert.strictEqual(requestAppHostStopRefresh.callCount, 2);
        assert.deepStrictEqual(requestAppHostStopRefresh.firstCall.args, [appHostPath]);
        assert.deepStrictEqual(requestAppHostStopRefresh.secondCall.args, [unknownAppHostPath]);
        provider.dispose();
    });

    test('workspace AppHost shows stopping state immediately after stop command', () => {
        const commands: AspireSubcommand[] = [];
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
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const [item] = provider.getChildren();

        provider.stopAppHost(item as any);

        const [stoppingItem] = provider.getChildren();
        assert.strictEqual(stoppingItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(stoppingItem.description, 'Stopping...');
        assert.strictEqual((stoppingItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.deepStrictEqual(commands, [['stop', '--apphost', shellArg(appHostPath)]]);
        provider.dispose();
    });

    test('workspace AppHost candidate shows stopping state immediately after stop command', () => {
        const commands: AspireSubcommand[] = [];
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
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const [groupItem] = provider.getChildren();
        const [item] = provider.getChildren(groupItem);

        provider.stopAppHost(item as any);

        const [updatedGroupItem] = provider.getChildren();
        const [updatedItem] = provider.getChildren(updatedGroupItem);
        assert.strictEqual(updatedItem.contextValue, 'workspaceResources:stopping');
        assert.strictEqual(updatedItem.description, 'Stopping...');
        assert.strictEqual((updatedItem.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        assert.deepStrictEqual(commands, [['stop', '--apphost', shellArg(appHostPath)]]);
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

            await provider.stopAppHost(workspaceItem as any);
            proof.run(commandLines[0], ['stop', '--apphost', appHostPath]);

            await provider.viewResourceLogs(resourceItem as any);
            proof.run(commandLines[1], ['logs', resourceName, '--apphost', appHostPath]);

            await provider.openResourceTerminal(resourceItem as any);
            proof.run(commandLines[2], ['terminal', 'attach', resourceName, '--apphost', appHostPath]);

            await provider.restartResource(resourceItem as any);
            proof.run(commandLines[3], ['resource', resourceName, 'restart', '--apphost', appHostPath]);
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

            await provider.stopAppHost(workspaceItem as any);
            proof.runPowerShell(commandLines[0], ['stop', '--apphost', appHostPath], powerShellPath);

            await provider.viewResourceLogs(resourceItem as any);
            proof.runPowerShell(commandLines[1], ['logs', resourceName, '--apphost', appHostPath], powerShellPath);

            await provider.openResourceTerminal(resourceItem as any);
            proof.runPowerShell(commandLines[2], ['terminal', 'attach', resourceName, '--apphost', appHostPath], powerShellPath);

            await provider.restartResource(resourceItem as any);
            proof.runPowerShell(commandLines[3], ['resource', resourceName, 'restart', '--apphost', appHostPath], powerShellPath);
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

            await assert.rejects(() => provider.stopAppHost(workspaceItem as any), { message: terminalCommandArgumentControlCharacters });
            await assert.rejects(() => provider.viewResourceLogs(resourceItem as any), { message: terminalCommandArgumentControlCharacters });
            await assert.rejects(() => provider.openResourceTerminal(resourceItem as any), { message: terminalCommandArgumentControlCharacters });
            await assert.rejects(() => provider.restartResource(resourceItem as any), { message: terminalCommandArgumentControlCharacters });

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
        } as unknown as AppHostLaunchService;
        const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = makeTreeProviderWithLaunchService([
            makeAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj', appHostPid: 1 }),
        ], launchService);

        await assert.rejects(provider.runAppHost({ appHostPath: '/workspace/AppHost/AppHost.csproj' } as any, true), /launch failed/);

        assert.strictEqual(showErrorStub.callCount, 1);
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
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());
        const infoStub = sandbox.stub(vscode.window, 'showInformationMessage');
        const [commandItem] = getResourceCommandItems(provider);

        await provider.executeResourceCommandItem(commandItem as any);

        assert.strictEqual(infoStub.called, false);
        assert.deepStrictEqual(sentCommands, [['resource', shellArg('my-service'), shellArg('$(legacy)'), '--apphost', shellArg('/test/AppHost.csproj')]]);
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
        // The repository eagerly probes `aspire config info --json` in its constructor. Stub it so
        // it doesn't spawn through the shared spawnCliProcess fake and clobber the discovery callback.
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves(null);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('workspace apphost name uses all candidates to disambiguate duplicate filenames', async () => {
        let lineCallback: ((line: string) => void) | undefined;
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            lineCallback = line => {
                options?.stdoutCallback?.(line);
                options?.exitCallback?.(0);
            };
            return { kill: () => { } } as any;
        });
        const repository = new AppHostDataRepository(makeTerminalProvider());

        try {
            await flushPromises();
            assert.ok(lineCallback);

            lineCallback(JSON.stringify({
                selected_project_file: '/workspace/apps/Store/AppHost.csproj',
                all_project_file_candidates: [
                    '/workspace/apps/Store/AppHost.csproj',
                    '/workspace/samples/Store/AppHost.csproj',
                ],
            }));
            await flushPromises();
            await new Promise(resolve => setTimeout(resolve, 0));
            await flushPromises();

            assert.strictEqual(repository.workspaceAppHostName, 'apps/Store/AppHost.csproj');
        } finally {
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

    test('FailedToStart shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart }));
        assert.strictEqual(icon.id, 'error');
    });

    test('RuntimeUnhealthy shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.RuntimeUnhealthy }));
        assert.strictEqual(icon.id, 'error');
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

    test('workspace mode renders selected non-running AppHost candidate without resources', () => {
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

        const [groupItem] = provider.getChildren();
        const result = provider.findAppHostElement('/repo/AppHost/AppHost.csproj');

        assert.ok(groupItem, 'Expected the Workspace AppHosts group');
        assert.strictEqual(groupItem.contextValue, 'workspaceAppHostsGroup');
        const [appHostItem] = provider.getChildren(groupItem);
        assert.strictEqual(appHostItem.label, 'AppHost.csproj');
        assert.strictEqual(appHostItem.contextValue, 'workspaceAppHost');
        assert.strictEqual(appHostItem.collapsibleState, vscode.TreeItemCollapsibleState.Collapsed);
        assert.deepStrictEqual(provider.getChildren(appHostItem).map(item => item.contextValue), [
            'workspaceAppHostAction:openSource',
            'workspaceAppHostAction:run',
            'workspaceAppHostAction:debug',
            'workspaceAppHostPath',
        ]);
        assert.deepStrictEqual(provider.getChildren(appHostItem).map(item => item.command?.command), [
            'aspire-vscode.openAppHostSource',
            'aspire-vscode.runAppHost',
            'aspire-vscode.debugAppHost',
            undefined,
        ]);
        assert.ok(result, 'Expected to find the workspace AppHost candidate');
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

    test('workspace mode renders launching AppHost with spinner and no context menu', () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const launchService = makeLaunchService();

        // Simulate the path being in launching state by calling launch (stub startDebugging)
        const stub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        launchService.launch(appHostPath, 'run', true);
        stub.restore();

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

        const [groupItem] = provider.getChildren();
        assert.strictEqual(groupItem?.contextValue, 'workspaceAppHostsGroup');
        const [item] = provider.getChildren(groupItem);

        assert.ok(item, 'Expected a launching workspace AppHost item');
        assert.strictEqual(item.contextValue, 'workspaceAppHostLaunching');
        assert.deepStrictEqual((item.iconPath as vscode.ThemeIcon).id, 'loading~spin');
        provider.dispose();
    });

    test('workspace mode groups idle AppHosts under WorkspaceAppHostsGroupItem when running AppHost exists', () => {
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

        // When both running and idle AppHosts exist, both sets are wrapped in sibling
        // groups so they nest at the same depth and read symmetrically in the tree.
        assert.strictEqual(topLevelItems.length, 2);
        assert.strictEqual(topLevelItems[0].contextValue, 'runningAppHostsGroup');
        assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHostsGroup');

        // Running group contains the running AppHost (rendered as WorkspaceResourcesItem)
        const runningChildren = provider.getChildren(topLevelItems[0]);
        assert.strictEqual(runningChildren.length, 1);
        assert.ok(runningChildren[0].contextValue?.startsWith('workspaceResources'));

        // Workspace group contains the idle AppHost
        const idleChildren = provider.getChildren(topLevelItems[1]);
        assert.strictEqual(idleChildren.length, 1);
        assert.strictEqual(idleChildren[0].contextValue, 'workspaceAppHost');
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

        // Both groups must appear: the .csproj candidate should be recognized as running
        // (rendered in the running group), and the unrelated idle candidate stays in
        // the workspace group. Without directory-equivalence matching, the .csproj
        // candidate would be misclassified as idle.
        assert.strictEqual(topLevelItems.length, 2);
        assert.strictEqual(topLevelItems[0].contextValue, 'runningAppHostsGroup');
        assert.strictEqual(topLevelItems[1].contextValue, 'workspaceAppHostsGroup');

        const runningChildren = provider.getChildren(topLevelItems[0]);
        assert.strictEqual(runningChildren.length, 1);
        assert.ok(runningChildren[0].contextValue?.startsWith('workspaceResources'));
        const resourceChildren = provider.getChildren(runningChildren[0]);
        assert.strictEqual(resourceChildren.length, 1);
        assert.strictEqual(resourceChildren[0].label, 'workspace-service');

        const idleChildren = provider.getChildren(topLevelItems[1]);
        assert.strictEqual(idleChildren.length, 1);
        assert.strictEqual(idleChildren[0].contextValue, 'workspaceAppHost');
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

        // Get the workspace item from inside the Workspace AppHosts group and pass it to runAppHost
        const [groupItem] = provider.getChildren();
        const [item] = provider.getChildren(groupItem);
        await provider.runAppHost(item as any, false);

        assert.ok(launchStub.calledOnce, 'Expected launch to be called');
        assert.strictEqual(launchStub.firstCall.args[0], appHostPath);
        assert.strictEqual(launchStub.firstCall.args[1], 'run');
        assert.strictEqual(launchStub.firstCall.args[2], false);
        launchStub.restore();
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

        const [groupItem] = provider.getChildren();
        const [item] = provider.getChildren(groupItem);
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

    test('workspace resource commands use the AppHost that owns the resource', () => {
        const commands: AspireSubcommand[] = [];
        const selectedHostPath = '/repo/apps/Store/AppHost.csproj';
        const otherHostPath = '/repo/samples/Store/AppHost.csproj';
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
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());

        const otherAppHostItem = provider.getChildren()[1];
        const resourcesGroup = provider.getChildren(otherAppHostItem).find(child => child.label === 'Resources');
        assert.ok(resourcesGroup, 'Expected resources group for second AppHost');
        const resourceItem = provider.getChildren(resourcesGroup)[0];

        provider.viewResourceLogs(resourceItem as any);
        provider.openResourceTerminal(resourceItem as any);
        provider.restartResource(resourceItem as any);

        assert.deepStrictEqual(commands, [
            ['logs', shellArg('cache'), '--apphost', shellArg(otherHostPath)],
            ['terminal', 'attach', shellArg('cache-b'), '--apphost', shellArg(otherHostPath)],
            ['resource', shellArg('cache-b'), shellArg('restart'), '--apphost', shellArg(otherHostPath)],
        ]);
        provider.dispose();
    });

    test('workspace resource commands use the running AppHost path when no workspace AppHost is selected', () => {
        const commands: AspireSubcommand[] = [];
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
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: AspireSubcommand) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, makeLaunchService());

        const [runningGroup] = provider.getChildren();
        const [runningAppHostItem] = provider.getChildren(runningGroup);
        const resourceItem = provider.getChildren(runningAppHostItem)[0];

        provider.viewResourceLogs(resourceItem as any);
        provider.openResourceTerminal(resourceItem as any);
        provider.restartResource(resourceItem as any);

        assert.deepStrictEqual(commands, [
            ['logs', shellArg('cache'), '--apphost', shellArg(runningHostPath)],
            ['terminal', 'attach', shellArg('cache'), '--apphost', shellArg(runningHostPath)],
            ['resource', shellArg('cache'), shellArg('restart'), '--apphost', shellArg(runningHostPath)],
        ]);
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
