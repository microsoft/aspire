import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { yesLabel } from '../loc/strings';
import { checkForExistingAppHostPathInWorkspace, getCommonExcludeGlob, findAspireSettingsFiles, getRelativePathToWorkspace } from '../utils/workspace';
import { AppHostDiscoveryService, getWorkspaceAppHostProjectSearchResult } from '../utils/appHostDiscovery';
import { getAppHostDiscoveryExcludeGlob } from '../utils/workspaceFileSearch';

suite('utils/workspace tests', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('getRelativePathToWorkspace falls back to the workspace name when asRelativePath cannot relativize', () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(workspaceFolder);
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
        const asRelativePathStub = sandbox.stub(vscode.workspace, 'asRelativePath');

        // `asRelativePath` returns the input unchanged when it cannot be made relative, and it
        // resolves against the workspace rather than the extension host. Both platforms' absolute
        // forms must be rejected here, because the Win32 forms are only recognized by `path.win32`
        // and the POSIX form only by `path.posix`.
        const hostAbsolutePath = path.join(path.sep, 'workspace', 'src', 'AppHost.csproj');
        const unrelativizedResults = [
            hostAbsolutePath,
            'C:\\Users\\me\\src\\AppHost.csproj',
            '\\\\server\\share\\src\\AppHost.csproj',
            '/home/me/src/AppHost.csproj',
        ].map(unrelativized => {
            asRelativePathStub.returns(unrelativized);
            return getRelativePathToWorkspace(hostAbsolutePath);
        });

        assert.deepStrictEqual(unrelativizedResults, ['workspace', 'workspace', 'workspace', 'workspace']);
    });

    test('getRelativePathToWorkspace reduces absolute paths from either platform to a bare file name', () => {
        // A path that is absolute only under the *other* platform's rules is inside no workspace
        // folder on this host, so `getWorkspaceFolder` returns undefined and the absolute-path
        // rejection in the `asRelativePath` branch is never reached. That left `path.basename`,
        // which on POSIX does not treat `\` as a separator, so the whole
        // `C:\Users\me\secret\AppHost.csproj` was returned as the debug configuration name.
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);

        const absolutePaths = [
            'C:\\Users\\me\\secret\\AppHost.csproj',
            'C:/Users/me/secret/AppHost.csproj',
            '\\\\server\\share\\secret\\AppHost.csproj',
            '/home/me/secret/AppHost.csproj',
        ];

        const names = absolutePaths.map(absolutePath => getRelativePathToWorkspace(absolutePath));

        assert.deepStrictEqual(names, ['AppHost.csproj', 'AppHost.csproj', 'AppHost.csproj', 'AppHost.csproj']);
        for (const name of names) {
            // The privacy property, asserted independently of host platform: whatever is returned
            // is a single path segment, so no directory component can leak.
            assert.ok(!/[\\/]/.test(name), `'${name}' must be a single path segment on either host platform`);
        }
    });

    test('getRelativePathToWorkspace keeps genuinely relative paths in either separator style', () => {
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(workspaceFolder);
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
        const asRelativePathStub = sandbox.stub(vscode.workspace, 'asRelativePath');

        const relativePaths = [
            'apps/Store/AppHost.csproj',
            'apps\\Store\\AppHost.csproj',
        ];

        const identities = relativePaths.map(relativePath => {
            asRelativePathStub.returns(relativePath);
            return getRelativePathToWorkspace('/workspace/apps/Store/AppHost.csproj');
        });

        assert.deepStrictEqual(identities, relativePaths, 'rejecting both absolute forms must not reject relative paths');
    });

    test('getRelativePathToWorkspace uses the file name when the path is outside every workspace folder', () => {
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);

        assert.strictEqual(getRelativePathToWorkspace(path.join(path.sep, 'elsewhere', 'src', 'AppHost.csproj')), 'AppHost.csproj');
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
            fs.rmSync(workspaceRoot, { recursive: true, force: true });
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
