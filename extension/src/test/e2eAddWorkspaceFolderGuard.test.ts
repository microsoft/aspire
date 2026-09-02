import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { getE2eAddableWorkspaceFolderPath } from '../testing/e2eStateFileBridge';

// `addWorkspaceFolder` exists to build a multi-root workspace during an E2E run, so the folder it
// receives is by definition not part of the workspace yet. Routing it through the guard that
// requires containment in an already-open folder made every workspace-target-proof shard fail with
// "workspace path arguments must stay inside the opened workspace" before a single assertion ran.
suite('E2E addWorkspaceFolder guard', () => {
    let runRoot: string;
    let workspaceRoot: string;
    let originalRunRoot: string | undefined;
    let originalWorkspaceRoot: string | undefined;

    setup(() => {
        runRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-addfolder-run-'));
        workspaceRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-addfolder-ws-'));
        originalRunRoot = process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT;
        originalWorkspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT;
        process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT = runRoot;
        process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT = workspaceRoot;
    });

    teardown(() => {
        restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_RUN_ROOT', originalRunRoot);
        restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT', originalWorkspaceRoot);
        fs.rmSync(runRoot, { recursive: true, force: true });
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
    });

    test('accepts a folder inside the workspace root that is not an open workspace folder', () => {
        const folderPath = path.join(workspaceRoot, 'folder-a');
        fs.mkdirSync(folderPath, { recursive: true });

        assert.strictEqual(getE2eAddableWorkspaceFolderPath(folderPath), folderPath);
    });

    test('accepts a folder inside the run root', () => {
        const folderPath = path.join(runRoot, 'folder-b');
        fs.mkdirSync(folderPath, { recursive: true });

        assert.strictEqual(getE2eAddableWorkspaceFolderPath(folderPath), folderPath);
    });

    test('accepts a contained folder whose name begins with two dots', () => {
        const folderPath = path.join(workspaceRoot, '..services');
        fs.mkdirSync(folderPath, { recursive: true });

        assert.strictEqual(getE2eAddableWorkspaceFolderPath(folderPath), folderPath);
    });

    test('rejects a folder outside every configured root', () => {
        const outsideRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-addfolder-outside-'));

        try {
            assert.throws(
                () => getE2eAddableWorkspaceFolderPath(outsideRoot),
                /can only add folders inside the configured E2E run root or workspace root/);
        } finally {
            fs.rmSync(outsideRoot, { recursive: true, force: true });
        }
    });

    test('rejects a path that is a file rather than a directory', () => {
        const filePath = path.join(workspaceRoot, 'not-a-folder.txt');
        fs.writeFileSync(filePath, 'contents');

        assert.throws(() => getE2eAddableWorkspaceFolderPath(filePath), /requires an existing folder/);
    });

    test('rejects a relative path', () => {
        assert.throws(() => getE2eAddableWorkspaceFolderPath('folder-a'), /requires an absolute folder path/);
    });
});

function restoreEnvironmentVariable(name: string, value: string | undefined): void {
    if (value === undefined) {
        delete process.env[name];
    } else {
        process.env[name] = value;
    }
}
