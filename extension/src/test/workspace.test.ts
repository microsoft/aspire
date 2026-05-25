import * as assert from 'assert';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import type { SpawnProcessOptions } from '../debugger/languages/cli';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { yesLabel } from '../loc/strings';
import { checkForExistingAppHostPathInWorkspace, findAppHostsWithAspireLs, getCommonExcludeGlob, findAspireSettingsFiles } from '../utils/workspace';

suite('utils/workspace tests', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
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
        assert.ok(glob.includes('**/.idea/**'), 'Should exclude .idea');
        assert.ok(glob.includes('**/.git/**'), 'Should exclude .git');
    });

    test('AppHost selection quick pick shows aspire ls language and status metadata', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let spawnOptions: SpawnProcessOptions | undefined;
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }]);
        sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
        sandbox.stub(vscode.window, 'showInformationMessage').resolves(yesLabel as never);
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            spawnOptions = options;
            return { kill: () => true } as ChildProcessWithoutNullStreams;
        });

        const disposable = await checkForExistingAppHostPathInWorkspace(terminalProvider, () => true, async () => { });
        assert.ok(spawnOptions);
        assert.ok(spawnOptions.stdoutCallback);
        assert.ok(spawnOptions.exitCallback);
        spawnOptions.stdoutCallback(JSON.stringify([
            {
                relativePath: 'apps/Store/AppHost.csproj',
                path: '/workspace/apps/Store/AppHost.csproj',
                language: 'csharp',
                status: 'buildable',
            },
            {
                relativePath: 'samples/Store/AppHost.csproj',
                path: '/workspace/samples/Store/AppHost.csproj',
                language: 'typescript/nodejs',
                status: 'possibly-unbuildable',
            },
        ]));
        spawnOptions.exitCallback(0);
        await waitForStubCall(showQuickPickStub);

        const items = showQuickPickStub.getCall(0).args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => ({
            label: item.label,
            description: item.description,
            detail: item.detail,
        })), [
            {
                label: 'apps/Store/AppHost.csproj',
                description: 'C# · buildable',
                detail: '/workspace/apps/Store/AppHost.csproj',
            },
            {
                label: 'samples/Store/AppHost.csproj',
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
            fs.writeFileSync(path.join(workspaceRoot, 'aspire.config.json'), JSON.stringify({
                appHost: {
                    path: configuredAppHostPath,
                },
            }));

            const terminalProvider = {
                getAspireCliExecutablePath: async () => 'aspire',
                createEnvironment: () => ({}),
            } as unknown as AspireTerminalProvider;
            let spawnOptions: SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                spawnOptions = options;
                return { kill: () => true } as ChildProcessWithoutNullStreams;
            });

            const rootFolder = {
                uri: vscode.Uri.file(workspaceRoot),
                name: 'workspace',
                index: 0,
            };
            const discovery = findAppHostsWithAspireLs(terminalProvider, 'aspire', rootFolder);

            assert.ok(spawnOptions);
            assert.ok(spawnOptions.stdoutCallback);
            assert.ok(spawnOptions.exitCallback);
            spawnOptions.stdoutCallback(JSON.stringify([
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
            spawnOptions.exitCallback(0);

            const result = await discovery.result;

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
