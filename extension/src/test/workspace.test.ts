import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { yesLabel } from '../loc/strings';
import { checkCliAvailableOrRedirect, checkForExistingAppHostPathInWorkspace, getCommonExcludeGlob, findAspireConfigFiles, findAspireSettingsFiles } from '../utils/workspace';
import { onDidResolveCliForOperation } from '../utils/cliOperationResolution';
import { AppHostDiscoveryService, getWorkspaceAppHostProjectSearchResult } from '../utils/appHostDiscovery';
import { appHostDiscoveryFindFilesMaxResults, getAppHostDiscoveryExcludeGlob } from '../utils/workspaceFileSearch';
import * as cliPathModule from '../utils/cliPath';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { createWorkspaceFolder, removeDirectorySafely } from './testHelpers';

// Finds the sinon call to vscode.workspace.findFiles whose glob pattern (first argument, either a
// raw string or a RelativePattern) contains the given substring. Used to pick out one specific
// glob walk (e.g. the legacy .aspire/settings.json search) among several concurrent findFiles calls.
function getFindFilesCallForPattern(findFilesStub: sinon.SinonStub, patternSubstring: string): sinon.SinonSpyCall | undefined {
    return findFilesStub.getCalls().find(call => {
        const include = call.args[0] as vscode.GlobPattern;
        const pattern = typeof include === 'string' ? include : include.pattern;
        return pattern.includes(patternSubstring);
    });
}

suite('utils/workspace tests', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    suite('checkCliAvailableOrRedirect', () => {
        test('forwards the supplied window target to resolveCliPath', async () => {
            const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: true, source: 'path' });

            const result = await checkCliAvailableOrRedirect('command_gate', windowCliPathTarget);

            assert.strictEqual(result.available, true);
            assert.ok(resolveCliPathStub.calledOnceWith(windowCliPathTarget));
        });

        test('forwards the supplied workspace folder target to resolveCliPath', async () => {
            const folder = createWorkspaceFolder('a', '/repo/a');
            const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: '/repo/a/bin/aspire', available: true, source: 'configured' });

            const result = await checkCliAvailableOrRedirect('debug_gate', workspaceFolderCliPathTarget(folder));

            assert.strictEqual(result.cliPath, '/repo/a/bin/aspire');
            assert.ok(resolveCliPathStub.calledOnceWith(workspaceFolderCliPathTarget(folder)));
        });

        test('uses pinnedCliPath without re-resolving the CLI', async () => {
            const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath');
            const tryExecuteCliStub = sandbox.stub(cliPathModule, 'tryExecuteCli').resolves(true);

            const result = await checkCliAvailableOrRedirect('debug_gate', windowCliPathTarget, { pinnedCliPath: '/repo/a/bin/aspire' });

            assert.strictEqual(result.cliPath, '/repo/a/bin/aspire');
            assert.ok(tryExecuteCliStub.calledOnceWithExactly('/repo/a/bin/aspire'));
            assert.strictEqual(resolveCliPathStub.called, false);
        });

        test('reports the exact CLI selected for an active command or debug operation', async () => {
            const target = workspaceFolderCliPathTarget(createWorkspaceFolder('a', '/repo/a'));
            sandbox.stub(cliPathModule, 'resolveCliPath').resolves({
                cliPath: '/repo/a/bin/aspire',
                available: true,
                source: 'configured',
            });
            const resolutions: Array<{ target: typeof target; cliPath: string }> = [];
            const disposable = onDidResolveCliForOperation(resolution => resolutions.push(resolution));

            try {
                await checkCliAvailableOrRedirect('debug_gate', target);

                assert.deepStrictEqual(resolutions, [{
                    target,
                    cliPath: '/repo/a/bin/aspire',
                }]);
            }
            finally {
                disposable.dispose();
            }
        });
    });

    test('getCommonExcludeGlob returns valid glob pattern', () => {
        const glob = getCommonExcludeGlob();

        assert.ok(glob.startsWith('{'), 'Glob should start with {');
        assert.ok(glob.endsWith('}'), 'Glob should end with }');
        assert.ok(glob.includes('**/node_modules/**'), 'Glob should include node_modules');
        assert.ok(glob.includes('**/[Bb]in/**'), 'Glob should include bin');
        assert.ok(glob.includes('**/[Oo]bj/**'), 'Glob should include obj');
        assert.ok(glob.includes('**/artifacts/**'), 'Glob should include artifacts');
    });

    test('findAspireSettingsFiles uses correct exclude pattern', async function () {
        this.timeout(10000);

        // Call findAspireSettingsFiles and verify it returns results (may be empty if no settings files exist)
        // The main point is that it executes without error and uses the exclude pattern
        const results = await findAspireSettingsFiles();

        // Results should be an array (possibly empty)
        assert.ok(Array.isArray(results), 'findAspireSettingsFiles should return an array');

        // Verify that any results found are not in excluded directories
        const excludeGlob = getCommonExcludeGlob();
        for (const uri of results) {
            const filePath = uri.fsPath;
            assert.ok(!filePath.includes('/node_modules/'), `Result should not be in node_modules: ${filePath}`);
            assert.ok(!filePath.includes('/bin/') && !filePath.includes('/Bin/'), `Result should not be in bin: ${filePath}`);
            assert.ok(!filePath.includes('/obj/') && !filePath.includes('/Obj/'), `Result should not be in obj: ${filePath}`);
            assert.ok(!filePath.includes('/artifacts/'), `Result should not be in artifacts: ${filePath}`);
        }
    });

    test('findAspireSettingsFiles bounds the legacy .aspire/settings.json glob walk via maxResults', async () => {
        sandbox.stub(vscode.workspace, 'findFiles').resolves([]);

        await findAspireSettingsFiles();

        const legacyCall = getFindFilesCallForPattern(vscode.workspace.findFiles as sinon.SinonStub, '.aspire/settings.json');
        assert.ok(legacyCall, 'expected a findFiles call for the legacy .aspire/settings.json glob');
        assert.strictEqual(legacyCall!.args[2], appHostDiscoveryFindFilesMaxResults, 'legacy settings.json walk should be bounded by appHostDiscoveryFindFilesMaxResults');
    });

    suite('findAspireConfigFiles', () => {
        test('bounds the aspire.config.json glob walk via maxResults', async () => {
            const findFilesStub = sandbox.stub(vscode.workspace, 'findFiles').resolves([]);

            await findAspireConfigFiles();

            assert.strictEqual(findFilesStub.callCount, 1);
            assert.strictEqual(findFilesStub.getCall(0).args[2], appHostDiscoveryFindFilesMaxResults, 'aspire.config.json walk should be bounded by appHostDiscoveryFindFilesMaxResults');
        });

        test('shares a single in-flight aspire.config.json glob walk across concurrent callers', async () => {
            let resolveConfigFindFiles: ((uris: vscode.Uri[]) => void) | undefined;
            const findFilesStub = sandbox.stub(vscode.workspace, 'findFiles').callsFake((include: vscode.GlobPattern) => {
                const pattern = typeof include === 'string' ? include : include.pattern;
                if (!pattern.includes('aspire.config.json')) {
                    return Promise.resolve([]);
                }

                return new Promise<vscode.Uri[]>(resolve => { resolveConfigFindFiles = resolve; });
            });

            // Mirrors extension.ts's activation sequence, where checkForExistingAppHostPathInWorkspace
            // (-> findAspireSettingsFiles) and AspirePackageRestoreProvider.activate() (-> _restoreAll ->
            // findAspireConfigFiles) both kick off discovery without awaiting one another first.
            const callerA = findAspireConfigFiles();
            const callerB = findAspireSettingsFiles();

            assert.ok(resolveConfigFindFiles, 'the stubbed aspire.config.json findFiles call should have started synchronously');
            resolveConfigFindFiles!([]);
            await Promise.all([callerA, callerB]);

            const configGlobCallCount = findFilesStub.getCalls().filter(call => {
                const pattern = typeof call.args[0] === 'string' ? call.args[0] : (call.args[0] as vscode.RelativePattern).pattern;
                return pattern.includes('aspire.config.json');
            }).length;
            assert.strictEqual(configGlobCallCount, 1, 'concurrent callers should share a single aspire.config.json glob walk instead of each starting their own');
        });
    });

    test('getCommonExcludeGlob includes all expected directories', () => {
        const glob = getCommonExcludeGlob();

        // Build outputs
        assert.ok(glob.includes('**/artifacts/**'), 'Should exclude artifacts');
        assert.ok(glob.includes('**/[Bb]in/**'), 'Should exclude bin (case-insensitive)');
        assert.ok(glob.includes('**/[Oo]bj/**'), 'Should exclude obj (case-insensitive)');
        assert.ok(glob.includes('**/dist/**'), 'Should exclude dist');
        assert.ok(glob.includes('**/out/**'), 'Should exclude out');
        assert.ok(glob.includes('**/build/**'), 'Should exclude build');
        assert.ok(glob.includes('**/publish/**'), 'Should exclude publish');

        // Dependencies
        assert.ok(glob.includes('**/node_modules/**'), 'Should exclude node_modules');
        assert.ok(glob.includes('**/.venv/**'), 'Should exclude .venv');
        assert.ok(glob.includes('**/packages/**'), 'Should exclude packages');

        // IDE/Tool directories
        assert.ok(glob.includes('**/.vs/**'), 'Should exclude .vs');
        assert.ok(glob.includes('**/.vscode-test/**'), 'Should exclude .vscode-test');
        assert.ok(glob.includes('**/.worktrees/**'), 'Should exclude git worktrees');
        assert.ok(glob.includes('**/.claude/**'), 'Should exclude agent worktrees');
        assert.ok(glob.includes('**/.agents/**'), 'Should exclude agent skills');
        assert.ok(glob.includes('**/.github/skills/**'), 'Should exclude GitHub agent skills');
        assert.ok(glob.includes('**/.opencode/skill/**'), 'Should exclude OpenCode agent skills');
        assert.ok(glob.includes('**/.idea/**'), 'Should exclude .idea');
        assert.ok(glob.includes('**/.git/**'), 'Should exclude .git');
    });

    test('getAppHostDiscoveryExcludeGlob skips user patterns that cannot be safely composed', () => {
        sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => ({
            get: () => section === 'files'
                ? {
                    '**/safe-generated/**': true,
                    '**/{generated,tmp}/**': true,
                }
                : {
                    '**/safe-search/**': true,
                    '**/coverage,dist/**': true,
                },
        } as unknown as vscode.WorkspaceConfiguration));

        const glob = getAppHostDiscoveryExcludeGlob();

        assert.ok(glob.includes('**/safe-generated/**'), 'Should include safe files.exclude pattern');
        assert.ok(glob.includes('**/safe-search/**'), 'Should include safe search.exclude pattern');
        assert.ok(!glob.includes('**/{generated,tmp}/**'), 'Should skip nested brace pattern');
        assert.ok(!glob.includes('**/coverage,dist/**'), 'Should skip comma pattern');
    });

    test('AppHost selection quick pick shows aspire ls language and status metadata', async () => {
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
        sandbox.stub(vscode.window, 'showInformationMessage').resolves(yesLabel as never);
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        const appHostDiscoveryService = createAppHostDiscoveryService([
            {
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            },
            {
                path: '/workspace/samples/Store/AppHost.csproj',
                language: 'typescript/nodejs',
                status: 'possibly-unbuildable',
            },
        ]);

        const disposable = await checkForExistingAppHostPathInWorkspace(appHostDiscoveryService, () => true, async () => { });
        await waitForStubCall(showQuickPickStub);

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => ({
            label: item.label,
            description: item.description,
            detail: item.detail,
        })), [
            {
                label: path.join('apps', 'Store', 'AppHost.csproj'),
                description: 'C# · buildable',
                detail: '/workspace/apps/Store/AppHost.csproj',
            },
            {
                label: path.join('samples', 'Store', 'AppHost.csproj'),
                description: 'TypeScript · possibly-unbuildable',
                detail: '/workspace/samples/Store/AppHost.csproj',
            },
        ]);

        disposable?.dispose();
    });

    test('aspire ls discovery preserves configured AppHost outside candidate results', async () => {
        const workspaceRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-extension-workspace-'));
        const configuredAppHostPath = path.join(path.dirname(workspaceRoot), 'external', 'AppHost.csproj');
        const discoveredAppHostPath = path.join(workspaceRoot, 'apps', 'Store', 'AppHost.csproj');
        const secondDiscoveredAppHostPath = path.join(workspaceRoot, 'samples', 'Store', 'AppHost.csproj');

        try {
            const configPath = path.join(workspaceRoot, 'aspire.config.json');
            fs.writeFileSync(configPath, JSON.stringify({
                appHost: {
                    path: configuredAppHostPath,
                },
            }));
            sandbox.stub(vscode.workspace, 'findFiles').callsFake(async (include) => {
                const pattern = typeof include === 'string' ? include : include.pattern;
                return pattern.includes('aspire.config.json') ? [vscode.Uri.file(configPath)] : [];
            });

            const rootFolder = {
                uri: vscode.Uri.file(workspaceRoot),
                name: 'workspace',
                index: 0,
            };
            const result = await getWorkspaceAppHostProjectSearchResult(rootFolder, [
                {
                    path: discoveredAppHostPath,
                    language: 'csharp',
                    status: 'buildable',
                },
                {
                    path: secondDiscoveredAppHostPath,
                    language: 'csharp',
                    status: 'buildable',
                },
                {
                    path: configuredAppHostPath,
                    language: null,
                    status: 'buildable',
                    selected: true,
                },
            ]);

            assert.strictEqual(result.selected_project_file, configuredAppHostPath);
            assert.deepStrictEqual(result.all_project_file_candidates, [
                discoveredAppHostPath,
                secondDiscoveredAppHostPath,
                configuredAppHostPath,
            ]);
            assert.deepStrictEqual(result.app_host_candidates.map(candidate => candidate.path), [
                discoveredAppHostPath,
                secondDiscoveredAppHostPath,
                configuredAppHostPath,
            ]);
            assert.deepStrictEqual(result.app_host_candidates.at(-1), {
                relativePath: path.relative(workspaceRoot, configuredAppHostPath),
                path: configuredAppHostPath,
                language: '',
                status: 'buildable',
            });
        } finally {
            removeDirectorySafely(workspaceRoot);
        }
    });
});

async function flushPromises(): Promise<void> {
    await new Promise(resolve => setImmediate(resolve));
}

async function waitForAppHostDiscovery(): Promise<void> {
    await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 0));
    await flushPromises();
}

async function waitForStubCall(stub: sinon.SinonStub): Promise<void> {
    for (let i = 0; i < 10 && !stub.called; i++) {
        await waitForAppHostDiscovery();
    }

    assert.ok(stub.called);
}

function createAppHostDiscoveryService(candidates: Awaited<ReturnType<AppHostDiscoveryService['discover']>>): AppHostDiscoveryService {
    return {
        onDidChangeCandidates: () => ({ dispose: () => { } }),
        discover: async () => candidates,
    } as unknown as AppHostDiscoveryService;
}
