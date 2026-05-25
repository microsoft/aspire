import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import { AppHostDataRepository, shortenPath, shortenPaths } from '../views/AppHostDataRepository';
import { AspireAppHostTreeProvider, getResourceContextValue, getResourceIcon, resolveAppHostSourcePath, buildResourceDescription } from '../views/AspireAppHostTreeProvider';
import type { AppHostDisplayInfo, ResourceJson, ViewMode } from '../views/AppHostDataRepository';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';

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
        workspaceAppHostName: undefined,
        workspaceAppHostDescription,
        onDidChangeData,
    } as unknown as AppHostDataRepository;

    return new AspireAppHostTreeProvider(repository, makeTerminalProvider());
}

function makeWorkspaceTreeProvider(workspaceAppHostDescription: string): AspireAppHostTreeProvider {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    const repository = {
        viewMode: 'workspace',
        appHosts: [],
        workspaceResources: [makeResource()],
        workspaceAppHostPath: '/workspace/apps/Store/AppHost.csproj',
        workspaceAppHostName: 'AppHost.csproj',
        workspaceAppHostDescription,
        onDidChangeData,
    } as unknown as AppHostDataRepository;

    return new AspireAppHostTreeProvider(repository, makeTerminalProvider());
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
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);

        await provider.openDashboard();

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
    });
});

suite('AppHostDataRepository', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
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
            commands: { 'start': { displayName: null, description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with resource-start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'resource-start': { displayName: null, description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with stop command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'stop': { displayName: null, description: null } },
        }));
        assert.strictEqual(result, 'resource:canStop');
    });

    test('resource with all lifecycle commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'start': { displayName: null, description: null },
                'stop': { displayName: null, description: null },
                'restart': { displayName: null, description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canStart:canStop:canRestart');
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
                'restart': { displayName: null, description: null },
                'custom-action': { displayName: null, description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canRestart');
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
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

        const result = provider.findAppHostElement(hostPath);

        assert.ok(result, 'Expected to find a WorkspaceResourcesItem');
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
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

        const result = provider.findAppHostElement('/repo/AppHost/AppHost.cs');

        assert.ok(result, 'Expected to find a WorkspaceResourcesItem via directory match');
        provider.dispose();
    });

    test('returns undefined for workspace mode without resources', () => {
        const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
        const repository = {
            viewMode: 'workspace' as ViewMode,
            appHosts: [],
            workspaceResources: [],
            workspaceAppHostPath: '/repo/AppHost/AppHost.csproj',
            workspaceAppHostName: undefined,
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

        const result = provider.findAppHostElement('/repo/AppHost/AppHost.csproj');

        // Workspace tree builds no top-level item when there are no resources, so no match.
        assert.strictEqual(result, undefined);
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
                resources: [],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

        const [appHostItem] = provider.getChildren();
        const appHostChildren = provider.getChildren(appHostItem);
        const result = provider.findAppHostElement(hostPath);

        assert.ok(appHostItem, 'Expected a workspace AppHost item');
        assert.strictEqual(appHostItem.label, 'AppHost.csproj');
        assert.strictEqual(appHostChildren.length, 3);
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
            hasMultipleWorkspaceAppHosts: true,
            workspaceAppHostName: undefined,
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

        const appHostItems = provider.getChildren();

        assert.deepStrictEqual(appHostItems.map(item => item.label), [
            'apps/Store/AppHost.csproj',
            'samples/Store/AppHost.csproj',
        ]);
        provider.dispose();
    });

    test('workspace resource commands use the AppHost that owns the resource', () => {
        const commands: string[] = [];
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
            hasMultipleWorkspaceAppHosts: true,
            workspaceAppHostName: 'apps/Store/AppHost.csproj',
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: (command: string) => commands.push(command),
        } as unknown as AspireTerminalProvider;
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider);

        const otherAppHostItem = provider.getChildren()[1];
        const resourcesGroup = provider.getChildren(otherAppHostItem).find(child => child.label === 'Resources');
        assert.ok(resourcesGroup, 'Expected resources group for second AppHost');
        const resourceItem = provider.getChildren(resourcesGroup)[0];

        provider.viewResourceLogs(resourceItem as any);
        provider.restartResource(resourceItem as any);

        assert.deepStrictEqual(commands, [
            `logs "cache" --apphost "${otherHostPath}"`,
            `resource "cache-b" restart --apphost "${otherHostPath}"`,
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
            hasMultipleWorkspaceAppHosts: true,
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

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
            hasMultipleWorkspaceAppHosts: true,
            workspaceAppHostName: 'AppHost.csproj',
            workspaceAppHostDescription: 'Workspace view selected because aspire ls found 2 buildable AppHosts.',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

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
                resources: [
                    makeResource({ name: 'api', displayName: 'api' }),
                    makeResource({ name: 'api-child', displayName: 'api-child', properties: { 'resource.parentName': 'api' } }),
                ],
            }),
            workspaceAppHostPath: hostPath,
            workspaceAppHostName: 'AppHost.csproj',
            onDidChangeData,
        } as unknown as AppHostDataRepository;
        const provider = new AspireAppHostTreeProvider(repository, makeTerminalProvider());

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

        const resultA = provider.findAppHostElement('/repo/A/A.cs');
        const resultB = provider.findAppHostElement(hostB);

        assert.ok(resultA);
        assert.ok(resultB);
        assert.notStrictEqual(resultA, resultB, 'Expected distinct items for distinct AppHosts');
        provider.dispose();
    });
});
