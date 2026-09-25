import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWorkspaceFolder } from './testHelpers';
import {
    ExpandedConfiguredCliPath,
    expandConfiguredCliPath,
    getCliExecutableCandidates,
    getCliPathTargetForUri,
    getCliPathTargetKey,
    windowCliPathTarget,
    workspaceFolderCliPathTarget,
} from '../utils/cliPathVariables';

function makeFolder(name: string, fsPath: string, index: number = 0): vscode.WorkspaceFolder {
    return createWorkspaceFolder(name, fsPath, index);
}

suite('cliPathVariables tests', () => {

    suite('CliPathResolutionTarget contract', () => {

        test('windowCliPathTarget has kind === "window"', () => {
            assert.strictEqual(windowCliPathTarget.kind, 'window');
        });

        test('workspaceFolderCliPathTarget has kind === "workspaceFolder" and correct workspaceFolder', () => {
            const folder = makeFolder('myRepo', '/repo');
            const target = workspaceFolderCliPathTarget(folder);

            assert.strictEqual(target.kind, 'workspaceFolder');
            if (target.kind === 'workspaceFolder') {
                assert.strictEqual(target.workspaceFolder, folder);
            }
        });

    });

    suite('createWorkspaceFolder', () => {

        test('reports fsPath exactly as written, whatever the host would render', () => {
            // vscode.Uri.file rewrites this on every platform: POSIX hosts prefix a separator, Windows
            // hosts flip them. The fixtures below describe POSIX paths and pass an explicit platform to
            // the code under test, so the folder has to keep the path it was given or those cases stop
            // exercising the platform argument at all.
            const written = 'C:\\repo\\a';
            const folder = createWorkspaceFolder('a', written);

            assert.notStrictEqual(vscode.Uri.file(written).fsPath, written);
            assert.strictEqual(folder.uri.fsPath, written);
            assert.strictEqual(folder.name, 'a');
            assert.strictEqual(folder.index, 0);
        });

        test('leaves the rest of the Uri intact', () => {
            const folder = createWorkspaceFolder('a', '/repo/a');

            assert.strictEqual(folder.uri.scheme, 'file');
            assert.strictEqual(folder.uri.path, '/repo/a');
        });

    });

    suite('expandConfiguredCliPath', () => {

        test('expands ${workspaceFolder} and normalizes path traversal on linux', () => {
            const configuredPath = '${workspaceFolder}/../../artifacts/bin/Aspire.Cli/Debug/net10.0/aspire';
            const folder = makeFolder('JavaSpringBoot', '/repo/playground/JavaSpringBoot');
            const target = workspaceFolderCliPathTarget(folder);

            const result = expandConfiguredCliPath(configuredPath, target, [folder], 'linux');

            assert.strictEqual(result.configuredPath, configuredPath);
            assert.strictEqual(result.resolvedPath, '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('expands ${workspaceFolder:tools} to the uniquely-named folder, independent of operation target', () => {
            const configuredPath = '${workspaceFolder:tools}/bin/aspire';
            const toolsFolder = makeFolder('tools', '/repo/tools', 1);
            const playgroundFolder = makeFolder('playground', '/repo/playground', 0);
            const target = workspaceFolderCliPathTarget(playgroundFolder);

            const result = expandConfiguredCliPath(configuredPath, target, [playgroundFolder, toolsFolder], 'linux');

            assert.strictEqual(result.resolvedPath, '/repo/tools/bin/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('window target with two open folders returns undefined resolvedPath and error mentioning multiple workspace folders', () => {
            const configuredPath = '${workspaceFolder}/bin/aspire';
            const folder1 = makeFolder('alpha', '/repo/alpha', 0);
            const folder2 = makeFolder('beta', '/repo/beta', 1);

            const result = expandConfiguredCliPath(configuredPath, windowCliPathTarget, [folder1, folder2], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
            assert.ok(
                result.error.toLowerCase().includes('multiple') || result.error.toLowerCase().includes('ambiguous'),
                `error should mention multiple folders; got: ${result.error}`
            );
        });

        test('plain relative path is returned unchanged with no resolvedPath or error', () => {
            const configuredPath = '../../artifacts/aspire';

            const result = expandConfiguredCliPath(configuredPath, windowCliPathTarget, [], 'linux');

            assert.strictEqual(result.configuredPath, configuredPath);
            assert.strictEqual(result.resolvedPath, undefined);
            assert.strictEqual(result.error, undefined);
        });

        test('window target in a single-folder workspace expands unqualified token', () => {
            const folder = makeFolder('repo', '/repo', 0);

            const result = expandConfiguredCliPath('${workspaceFolder}/bin/aspire', windowCliPathTarget, [folder], 'linux');

            assert.strictEqual(result.resolvedPath, '/repo/bin/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('unknown token is rejected with an error', () => {
            const result = expandConfiguredCliPath('${customVar}/aspire', windowCliPathTarget, [], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
        });

        test('missing named folder is rejected with an error', () => {
            const folder = makeFolder('root', '/repo', 0);

            const result = expandConfiguredCliPath('${workspaceFolder:nonexistent}/aspire', windowCliPathTarget, [folder], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
        });

        test('ambiguous named folder (two folders with same name) is rejected with an error', () => {
            const folder1 = makeFolder('tools', '/repo/alpha/tools', 0);
            const folder2 = makeFolder('tools', '/repo/beta/tools', 1);

            const result = expandConfiguredCliPath('${workspaceFolder:tools}/aspire', windowCliPathTarget, [folder1, folder2], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
            assert.ok(
                result.error.toLowerCase().includes('ambiguous') || result.error.toLowerCase().includes('multiple'),
                `error should mention ambiguity; got: ${result.error}`
            );
        });

    });

    suite('getCliExecutableCandidates', () => {

        test('Windows extensionless path returns exact, .exe, .cmd, .bat in order', () => {
            const candidates = getCliExecutableCandidates('C:\\tools\\aspire', 'win32');

            assert.deepStrictEqual(candidates, [
                'C:\\tools\\aspire',
                'C:\\tools\\aspire.exe',
                'C:\\tools\\aspire.cmd',
                'C:\\tools\\aspire.bat',
                'C:\\tools\\aspire\\aspire',
                'C:\\tools\\aspire\\aspire.exe',
                'C:\\tools\\aspire\\aspire.cmd',
                'C:\\tools\\aspire\\aspire.bat',
            ]);
        });

        test('Windows path already ending in .exe returns only itself', () => {
            const candidates = getCliExecutableCandidates('C:\\tools\\aspire.exe', 'win32');

            assert.deepStrictEqual(candidates, ['C:\\tools\\aspire.exe']);
        });

        test('non-Windows path also probes the CLI inside it so a build output directory resolves', () => {
            const candidates = getCliExecutableCandidates('/usr/local/bin/aspire', 'linux');

            assert.deepStrictEqual(candidates, [
                '/usr/local/bin/aspire',
                '/usr/local/bin/aspire/aspire',
            ]);
        });

        test('directory path resolves the CLI executable inside it', () => {
            const candidates = getCliExecutableCandidates('/repo/artifacts/bin/Aspire.Cli/Debug/net10.0', 'darwin');

            assert.deepStrictEqual(candidates, [
                '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0',
                '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
            ]);
        });

        test('windows directory whose last segment contains a dot still probes the executable inside it', () => {
            // extname('net10.0') is '.0', so treating a dotted final segment as a file name meant a
            // locally built CLI at ...\\Debug\\net10.0 was never found on Windows.
            const candidates = getCliExecutableCandidates('C:\\repo\\artifacts\\bin\\Aspire.Cli\\Debug\\net10.0', 'win32');

            assert.ok(candidates.includes('C:\\repo\\artifacts\\bin\\Aspire.Cli\\Debug\\net10.0\\aspire.exe'));
        });

    });

    suite('getCliPathTargetKey', () => {

        test('window target key is "window"', () => {
            assert.strictEqual(getCliPathTargetKey(windowCliPathTarget), 'window');
        });

        test('workspaceFolder target key is "workspaceFolder:${folder.uri.toString()}"', () => {
            const folder = makeFolder('myFolder', '/repo');
            const target = workspaceFolderCliPathTarget(folder);

            assert.strictEqual(getCliPathTargetKey(target), `workspaceFolder:${folder.uri.toString()}`);
        });

    });

    suite('getCliPathTargetForUri', () => {
        let sandbox: sinon.SinonSandbox;

        setup(() => {
            sandbox = sinon.createSandbox();
        });

        teardown(() => {
            sandbox.restore();
        });

        test('returns the owning workspace folder target when a folder owns the uri', () => {
            const folder = makeFolder('java', '/repo/playground/JavaSpringBoot');
            sandbox.stub(vscode.workspace, 'getWorkspaceFolder').withArgs(sinon.match.any).returns(folder);

            const target = getCliPathTargetForUri(vscode.Uri.file('/repo/playground/JavaSpringBoot/AppHost.csproj'));

            assert.strictEqual(target.kind, 'workspaceFolder');
            if (target.kind === 'workspaceFolder') {
                assert.strictEqual(target.workspaceFolder, folder);
            }
        });

        test('returns the window target when no open folder owns the uri', () => {
            sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);

            const target = getCliPathTargetForUri(vscode.Uri.file('/outside/AppHost.csproj'));

            assert.deepStrictEqual(target, windowCliPathTarget);
        });

    });

});
