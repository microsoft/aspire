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

function makeTreeProvider(appHosts: readonly AppHostDisplayInfo[], viewMode: ViewMode = 'global'): AspireAppHostTreeProvider {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    const repository = {
        viewMode,
        appHosts,
        workspaceResources: [],
        workspaceAppHostPath: undefined,
        workspaceAppHostName: undefined,
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
            lineCallback = options?.lineCallback;
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
            commands: { 'start': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with resource-start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'resource-start': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with stop command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'stop': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStop');
    });

    test('resource with all lifecycle commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'start': { description: null },
                'stop': { description: null },
                'restart': { description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canStart:canStop:canRestart');
    });

    test('resource with non-lifecycle commands has base context only', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'custom-action': { description: 'do something' } },
        }));
        assert.strictEqual(result, 'resource');
    });

    test('resource with mixed lifecycle and custom commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'restart': { description: null },
                'custom-action': { description: null },
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

    test('returns undefined when AppHost lives in a different directory', () => {
        const provider = makeTreeProvider([makeAppHost({ appHostPath: '/elsewhere/Other.csproj' })]);

        const result = provider.findAppHostElement('/repo/AppHost/AppHost.cs');

        assert.strictEqual(result, undefined);
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
