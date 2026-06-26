import * as assert from 'assert';
import * as sinon from 'sinon';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { getDefaultCliInstallPaths, resolveCliPath, CliPathDependencies, tryExecuteCli } from '../utils/cliPath';

const bundlePath = '/home/user/.aspire/bin/aspire';
const globalToolPath = '/home/user/.dotnet/tools/aspire';
const defaultPaths = [bundlePath, globalToolPath];

function createMockDeps(overrides: Partial<CliPathDependencies> = {}): CliPathDependencies {
    return {
        getConfiguredPath: () => '',
        getDefaultPaths: () => defaultPaths,
        getWorkspaceFolders: () => [],
        isOnPath: async () => false,
        findAtDefaultPath: async () => undefined,
        tryExecute: async () => false,
        setConfiguredPath: async () => {},
        ...overrides,
    };
}

suite('utils/cliPath tests', () => {

    suite('getDefaultCliInstallPaths', () => {
        test('returns bundle path (~/.aspire/bin) as first entry', () => {
            const paths = getDefaultCliInstallPaths();
            const homeDir = os.homedir();

            assert.ok(paths.length >= 2, 'Should return at least 2 default paths');
            assert.ok(paths[0].startsWith(path.join(homeDir, '.aspire', 'bin')), `First path should be bundle install: ${paths[0]}`);
        });

        test('returns global tool path (~/.dotnet/tools) as second entry', () => {
            const paths = getDefaultCliInstallPaths();
            const homeDir = os.homedir();

            assert.ok(paths[1].startsWith(path.join(homeDir, '.dotnet', 'tools')), `Second path should be global tool: ${paths[1]}`);
        });

        test('uses correct executable name for current platform', () => {
            const paths = getDefaultCliInstallPaths();

            for (const p of paths) {
                const basename = path.basename(p);
                if (process.platform === 'win32') {
                    assert.strictEqual(basename, 'aspire.exe');
                } else {
                    assert.strictEqual(basename, 'aspire');
                }
            }
        });
    });

    suite('resolveCliPath', () => {
        let originalE2eCliPath: string | undefined;

        setup(() => {
            originalE2eCliPath = process.env.ASPIRE_EXTENSION_E2E_CLI_PATH;
            delete process.env.ASPIRE_EXTENSION_E2E_CLI_PATH;
        });

        teardown(() => {
            if (originalE2eCliPath === undefined) {
                delete process.env.ASPIRE_EXTENSION_E2E_CLI_PATH;
            }
            else {
                process.env.ASPIRE_EXTENSION_E2E_CLI_PATH = originalE2eCliPath;
            }
        });

        test('prefers E2E-provided CLI path over settings and PATH', async () => {
            const e2ePath = '/tmp/e2e/aspire';
            process.env.ASPIRE_EXTENSION_E2E_CLI_PATH = e2ePath;
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => '/configured/path/aspire',
                isOnPath: async () => true,
                tryExecute: async (p) => p === e2ePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, e2ePath);
            assert.ok(setConfiguredPath.notCalled, 'should not rewrite settings for the E2E override path');
        });

        test('falls back to default install path when CLI is not on PATH', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                isOnPath: async () => false,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'default-install');
            assert.strictEqual(result.cliPath, bundlePath);
            assert.ok(setConfiguredPath.calledOnceWith(bundlePath), 'should update the VS Code setting to the found path');
        });

        test('updates VS Code setting when CLI found at default path but not on PATH', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => '',
                isOnPath: async () => false,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            await resolveCliPath(deps);

            assert.ok(setConfiguredPath.calledOnce, 'setConfiguredPath should be called once');
            assert.strictEqual(setConfiguredPath.firstCall.args[0], bundlePath, 'should set the path to the found install location');
        });

        test('prefers PATH over default install path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                isOnPath: async () => true,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire');
            assert.ok(setConfiguredPath.notCalled, 'should not update settings when CLI is on PATH');
        });

        test('uses configured bare Aspire command as a PATH command', async () => {
            const workspaceFolder = path.resolve('repo');
            const tryExecute = sinon.stub().callsFake(async candidatePath => candidatePath === 'aspire.cmd');
            const isOnPath = sinon.stub().resolves(false);
            const deps = createMockDeps({
                getConfiguredPath: () => 'aspire.cmd',
                getWorkspaceFolders: () => [workspaceFolder],
                tryExecute,
                isOnPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire.cmd');
            assert.deepStrictEqual(tryExecute.getCalls().map(call => call.args[0]), ['aspire.cmd']);
            assert.ok(isOnPath.notCalled, 'should not continue to the generic PATH probe when the configured bare command works');
        });

        test('falls back to normal PATH when configured bare Aspire command is unavailable', async () => {
            const workspaceFolder = path.resolve('repo');
            const tryExecute = sinon.stub().resolves(false);
            const isOnPath = sinon.stub().resolves(true);
            const deps = createMockDeps({
                getConfiguredPath: () => 'aspire.cmd',
                getWorkspaceFolders: () => [workspaceFolder],
                tryExecute,
                isOnPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire');
            assert.deepStrictEqual(tryExecute.getCalls().map(call => call.args[0]), ['aspire.cmd']);
            assert.ok(isOnPath.calledOnce, 'should fall through to the generic PATH probe');
        });

        test('clears setting when CLI is on PATH and setting was previously set to a default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                isOnPath: async () => true,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.ok(setConfiguredPath.calledOnceWith(''), 'should clear the setting');
        });

        test('clears setting when CLI is on PATH and setting was previously set to global tool path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => globalToolPath,
                isOnPath: async () => true,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.ok(setConfiguredPath.calledOnceWith(''), 'should clear the setting');
        });

        test('returns not-found when CLI is not on PATH and not at any default path', async () => {
            const deps = createMockDeps({
                isOnPath: async () => false,
                findAtDefaultPath: async () => undefined,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, false);
            assert.strictEqual(result.source, 'not-found');
        });

        test('uses custom configured path when valid and not a default', async () => {
            const customPath = '/custom/path/aspire';

            const deps = createMockDeps({
                getConfiguredPath: () => customPath,
                tryExecute: async (p) => p === customPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, customPath);
        });

        test('reports custom configured path as unavailable when it is invalid', async () => {
            const isOnPath = sinon.stub().resolves(true);
            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                isOnPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.available, false);
            assert.strictEqual(result.cliPath, '/bad/path/aspire');
            assert.ok(isOnPath.notCalled, 'should not silently fall back to PATH when the configured path is invalid');
        });

        test('resolves relative custom configured path against the first workspace folder', async () => {
            const workspaceFolder = path.resolve('repo');
            const relativeCliPath = path.join('artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', 'aspire');
            const resolvedCliPath = path.join(workspaceFolder, relativeCliPath);
            const isOnPath = sinon.stub().resolves(true);
            const getWorkspaceFolders = () => [workspaceFolder];
            const deps = createMockDeps({
                getConfiguredPath: () => relativeCliPath,
                tryExecute: async (candidatePath) => candidatePath === resolvedCliPath,
                isOnPath,
                getWorkspaceFolders,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.available, true);
            assert.strictEqual(result.cliPath, resolvedCliPath);
            assert.ok(isOnPath.notCalled, 'should not use PATH when a relative configured path resolves successfully');
        });

        test('reports custom configured path as unavailable instead of falling through to default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                isOnPath: async () => false,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.available, false);
            assert.strictEqual(result.cliPath, '/bad/path/aspire');
            assert.ok(setConfiguredPath.notCalled, 'should not rewrite settings while a configured path is invalid');
        });

        test('does not update setting when already set to the found default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                isOnPath: async () => false,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.ok(setConfiguredPath.notCalled, 'should not re-set the path if it already matches');
        });
    });

    suite('tryExecuteCli', () => {
        test('validates Windows cmd wrappers', async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }

            const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-path-test with spaces-'));
            try {
                const wrapperPath = path.join(tempDirectory, 'aspire.cmd');
                fs.writeFileSync(wrapperPath, '@echo off\r\nif "%~1"=="--version" (\r\n  echo 13.5.0-pr.e2e\r\n  exit /b 0\r\n)\r\nexit /b 1\r\n');

                assert.strictEqual(await tryExecuteCli(wrapperPath), true);
            }
            finally {
                fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
            }
        });
    });
});
