import * as assert from 'assert';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';
import * as sinon from 'sinon';

import { configureLaunchJsonCommand } from '../commands/configureLaunchJson';
import type { AppHostDiscoveryService, CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';

suite('configureLaunchJsonCommand', () => {
    test('prompts for dashboard launch behavior and writes it to launch.json', async () => {
        const sandbox = sinon.createSandbox();
        const tempRoot = await createTestWorkspace();

        try {
            const workspaceFolder = {
                uri: vscode.Uri.file(tempRoot),
                name: 'workspace',
                index: 0,
            } as vscode.WorkspaceFolder;

            sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => {
                const quickPickItems = items as readonly (vscode.QuickPickItem & { value: string })[];

                return quickPickItems.find(item => item.value === 'notification');
            });
            sandbox.stub(vscode.workspace, 'openTextDocument').resolves({} as vscode.TextDocument);
            sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);

            await configureLaunchJsonCommand(createDiscovery());

            const launchJsonPath = path.join(tempRoot, '.vscode', 'launch.json');
            const launchJson = JSON.parse(await fs.readFile(launchJsonPath, 'utf8'));
            assert.strictEqual(showQuickPickStub.callCount, 1);
            assert.strictEqual(launchJson.configurations[0].dashboardBrowser, 'notification');
            assert.strictEqual(launchJson.configurations[0].program, '${workspaceFolder}');
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });

    test('does not write launch.json when dashboard launch behavior selection is canceled', async () => {
        const sandbox = sinon.createSandbox();
        const tempRoot = await createTestWorkspace();

        try {
            const workspaceFolder = {
                uri: vscode.Uri.file(tempRoot),
                name: 'workspace',
                index: 0,
            } as vscode.WorkspaceFolder;

            sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
            sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);

            await assert.rejects(
                () => configureLaunchJsonCommand(createDiscovery()),
                error => error instanceof vscode.CancellationError
            );

            const launchJsonPath = path.join(tempRoot, '.vscode', 'launch.json');
            await assert.rejects(() => fs.stat(launchJsonPath), (error: NodeJS.ErrnoException) => error.code === 'ENOENT');
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });

    test('does not prompt for dashboard launch behavior when Aspire launch configuration already exists', async () => {
        const sandbox = sinon.createSandbox();
        const tempRoot = await createTestWorkspace();

        try {
            const vscodeDir = path.join(tempRoot, '.vscode');
            const launchJsonPath = path.join(vscodeDir, 'launch.json');
            await fs.mkdir(vscodeDir);
            const originalLaunchJson = `{
  // Existing launch.json files often contain comments because VS Code treats them as JSONC.
  "version": "0.2.0",
  "configurations": [
    {
      "type": "aspire",
      "request": "launch",
      "name": "Custom Aspire AppHost",
      "program": "\${workspaceFolder}"
    }
  ]
}
`;
            await fs.writeFile(launchJsonPath, originalLaunchJson);

            const workspaceFolder = {
                uri: vscode.Uri.file(tempRoot),
                name: 'workspace',
                index: 0,
            } as vscode.WorkspaceFolder;

            sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
            const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);
            sandbox.stub(vscode.workspace, 'openTextDocument').resolves({} as vscode.TextDocument);
            sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);

            await configureLaunchJsonCommand(createDiscovery());

            assert.strictEqual(showQuickPickStub.callCount, 0);
            assert.strictEqual(showInformationMessageStub.callCount, 1);
            assert.strictEqual(await fs.readFile(launchJsonPath, 'utf8'), originalLaunchJson);
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });

    test('writes launch.json into the workspace folder containing the only AppHost', async () => {
        const sandbox = sinon.createSandbox();
        const firstRoot = await createTestWorkspace();
        const secondRoot = await createTestWorkspace();

        try {
            const [first, second] = [firstRoot, secondRoot].map(createFolder);
            const appHostPath = path.join(secondRoot, 'AppHost', 'AppHost.csproj');
            stubWorkspace(sandbox, [first, second]);
            const showQuickPickStub = stubQuickPick(sandbox);

            await configureLaunchJsonCommand(createDiscovery({ [secondRoot]: [buildable(appHostPath)] }));

            assert.strictEqual(showQuickPickStub.callCount, 1);
            await assertMissing(path.join(firstRoot, '.vscode'));
            const launchJson = await readLaunchJson(secondRoot);
            assert.strictEqual(launchJson.configurations[0].program, '${workspaceFolder}/AppHost/AppHost.csproj');
        }
        finally {
            sandbox.restore();
            await fs.rm(firstRoot, { recursive: true, force: true });
            await fs.rm(secondRoot, { recursive: true, force: true });
        }
    });

    test('prompts for the AppHost when several are found and records the selection', async () => {
        const sandbox = sinon.createSandbox();
        const firstRoot = await createTestWorkspace();
        const secondRoot = await createTestWorkspace();

        try {
            const [first, second] = [firstRoot, secondRoot].map(createFolder);
            const firstAppHost = path.join(firstRoot, 'apphost.ts');
            const secondAppHost = path.join(secondRoot, 'Store', 'Store.AppHost.csproj');
            stubWorkspace(sandbox, [first, second]);
            const offered: string[] = [];
            stubQuickPick(sandbox, items => {
                offered.push(...items.map(item => item.detail ?? ''));
                return items.find(item => item.detail === secondAppHost);
            });

            await configureLaunchJsonCommand(createDiscovery({ [firstRoot]: [buildable(firstAppHost)], [secondRoot]: [buildable(secondAppHost)] }));

            assert.deepStrictEqual(offered, [firstAppHost, secondAppHost]);
            await assertMissing(path.join(firstRoot, '.vscode'));
            const launchJson = await readLaunchJson(secondRoot);
            assert.strictEqual(launchJson.configurations[0].program, '${workspaceFolder}/Store/Store.AppHost.csproj');
        }
        finally {
            sandbox.restore();
            await fs.rm(firstRoot, { recursive: true, force: true });
            await fs.rm(secondRoot, { recursive: true, force: true });
        }
    });

    test('does not write launch.json when AppHost selection is canceled', async () => {
        const sandbox = sinon.createSandbox();
        const tempRoot = await createTestWorkspace();

        try {
            stubWorkspace(sandbox, [createFolder(tempRoot, 0)]);
            const showQuickPickStub = stubQuickPick(sandbox, () => undefined);
            const candidates = [buildable(path.join(tempRoot, 'A', 'A.csproj')), buildable(path.join(tempRoot, 'B', 'B.csproj'))];

            await assert.rejects(
                () => configureLaunchJsonCommand(createDiscovery({ [tempRoot]: candidates })),
                error => error instanceof vscode.CancellationError
            );

            assert.strictEqual(showQuickPickStub.callCount, 1);
            await assertMissing(path.join(tempRoot, '.vscode'));
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });

    test('uses the selected default AppHost without prompting for one', async () => {
        const sandbox = sinon.createSandbox();
        const tempRoot = await createTestWorkspace();

        try {
            stubWorkspace(sandbox, [createFolder(tempRoot, 0)]);
            const showQuickPickStub = stubQuickPick(sandbox);
            const selected = { ...buildable(path.join(tempRoot, 'B', 'B.csproj')), selected: true };

            await configureLaunchJsonCommand(createDiscovery({ [tempRoot]: [buildable(path.join(tempRoot, 'A', 'A.csproj')), selected] }));

            assert.strictEqual(showQuickPickStub.callCount, 1);
            const launchJson = await readLaunchJson(tempRoot);
            assert.strictEqual(launchJson.configurations[0].program, '${workspaceFolder}/B/B.csproj');
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });

    test('prefers the AppHost open in the active editor', async () => {
        const sandbox = sinon.createSandbox();
        const firstRoot = await createTestWorkspace();
        const secondRoot = await createTestWorkspace();

        try {
            const [first, second] = [firstRoot, secondRoot].map(createFolder);
            const activeAppHost = path.join(secondRoot, 'apphost.ts');
            stubWorkspace(sandbox, [first, second], vscode.Uri.file(activeAppHost));
            const showQuickPickStub = stubQuickPick(sandbox);

            await configureLaunchJsonCommand(createDiscovery({ [firstRoot]: [buildable(path.join(firstRoot, 'apphost.ts'))], [secondRoot]: [buildable(activeAppHost)] }));

            assert.strictEqual(showQuickPickStub.callCount, 1);
            const launchJson = await readLaunchJson(secondRoot);
            assert.strictEqual(launchJson.configurations[0].program, '${workspaceFolder}/apphost.ts');
        }
        finally {
            sandbox.restore();
            await fs.rm(firstRoot, { recursive: true, force: true });
            await fs.rm(secondRoot, { recursive: true, force: true });
        }
    });
});

type TestQuickPickItem = vscode.QuickPickItem & { value?: string };

function createFolder(root: string, index: number): vscode.WorkspaceFolder {
    return { uri: vscode.Uri.file(root), name: path.basename(root), index };
}

function buildable(appHostPath: string): CandidateAppHostDisplayInfo {
    return { path: appHostPath, language: path.extname(appHostPath) === '.ts' ? 'typescript' : 'csharp', status: 'buildable' };
}

function createDiscovery(candidatesByRoot: Record<string, CandidateAppHostDisplayInfo[]> = {}): AppHostDiscoveryService {
    const all = Object.values(candidatesByRoot).flat();
    return {
        discover: async (folder: vscode.WorkspaceFolder) => candidatesByRoot[folder.uri.fsPath] ?? [],
        tryFindCandidateForEditorFile: async (filePath: string) => all.find(candidate => candidate.path === filePath),
    } as unknown as AppHostDiscoveryService;
}

function stubWorkspace(sandbox: sinon.SinonSandbox, folders: vscode.WorkspaceFolder[], activeUri?: vscode.Uri): void {
    sandbox.stub(vscode.workspace, 'workspaceFolders').value(folders);
    sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake(uri => folders.find(folder => uri.fsPath.startsWith(folder.uri.fsPath + path.sep)));
    // No aspire.config.json files, so the configured-default lookup finds nothing.
    sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
    sandbox.stub(vscode.window, 'activeTextEditor').value(activeUri ? { document: { uri: activeUri } } : undefined);
    sandbox.stub(vscode.workspace, 'openTextDocument').resolves({} as vscode.TextDocument);
    sandbox.stub(vscode.window, 'showTextDocument').resolves({} as vscode.TextEditor);
}

// Answers the dashboard prompt with 'notification' and delegates any AppHost prompt to pickAppHost.
function stubQuickPick(sandbox: sinon.SinonSandbox, pickAppHost: (items: readonly TestQuickPickItem[]) => TestQuickPickItem | undefined = () => assert.fail('unexpected AppHost prompt')): sinon.SinonStub {
    return sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => {
        const quickPickItems = items as readonly TestQuickPickItem[];
        return quickPickItems.some(item => item.value !== undefined)
            ? quickPickItems.find(item => item.value === 'notification')
            : pickAppHost(quickPickItems);
    });
}

async function readLaunchJson(root: string) {
    return JSON.parse(await fs.readFile(path.join(root, '.vscode', 'launch.json'), 'utf8'));
}

async function assertMissing(filePath: string) {
    await assert.rejects(() => fs.stat(filePath), (error: NodeJS.ErrnoException) => error.code === 'ENOENT');
}

async function createTestWorkspace() {
    const tempRoot = path.resolve(__dirname, '..', '..', '.test-workspaces', 'configure-launch-json');
    await fs.mkdir(tempRoot, { recursive: true });

    return await fs.mkdtemp(path.join(tempRoot, 'workspace-'));
}
