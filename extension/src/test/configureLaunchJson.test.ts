import * as assert from 'assert';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';
import * as sinon from 'sinon';

import { configureLaunchJsonCommand } from '../commands/configureLaunchJson';

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

            await configureLaunchJsonCommand();

            const launchJsonPath = path.join(tempRoot, '.vscode', 'launch.json');
            const launchJson = JSON.parse(await fs.readFile(launchJsonPath, 'utf8'));
            assert.strictEqual(showQuickPickStub.callCount, 1);
            assert.strictEqual(launchJson.configurations[0].dashboardBrowser, 'notification');
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
                () => configureLaunchJsonCommand(),
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
            await fs.writeFile(launchJsonPath, JSON.stringify({
                version: '0.2.0',
                configurations: [
                    {
                        type: 'aspire',
                        request: 'launch',
                        name: 'Aspire: Launch default AppHost',
                        program: '${workspaceFolder}'
                    }
                ]
            }));

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

            await configureLaunchJsonCommand();

            assert.strictEqual(showQuickPickStub.callCount, 0);
            assert.strictEqual(showInformationMessageStub.callCount, 1);
        }
        finally {
            sandbox.restore();
            await fs.rm(tempRoot, { recursive: true, force: true });
        }
    });
});

async function createTestWorkspace() {
    const tempRoot = path.resolve(__dirname, '..', '..', '.test-workspaces', 'configure-launch-json');
    await fs.mkdir(tempRoot, { recursive: true });

    return await fs.mkdtemp(path.join(tempRoot, 'workspace-'));
}
