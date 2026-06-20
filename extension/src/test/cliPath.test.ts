import * as assert from 'assert';
import * as sinon from 'sinon';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { getDefaultCliInstallPaths, resolveCliPath, CliPathDependencies, tryExecuteCli } from '../utils/cliPath';
import { getCliExecutionCommand } from '../utils/cliExecution';

const bundlePath = '/home/user/.aspire/bin/aspire';
const globalToolPath = '/home/user/.dotnet/tools/aspire';
const defaultPaths = [bundlePath, globalToolPath];

function createMockDeps(overrides: Partial<CliPathDependencies> = {}): CliPathDependencies {
    return {
        getConfiguredPath: () => '',
        getDefaultPaths: () => defaultPaths,
        findOnPath: async () => undefined,
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

        test('includes global tool path (~/.dotnet/tools)', () => {
            const paths = getDefaultCliInstallPaths();
            const homeDir = os.homedir();

            assert.ok(paths.some(p => p.startsWith(path.join(homeDir, '.dotnet', 'tools'))), `Should include global tool path: ${paths.join(', ')}`);
        });

        test('uses correct executable name for current platform', () => {
            const paths = getDefaultCliInstallPaths();

            for (const p of paths) {
                const basename = path.basename(p);
                if (process.platform === 'win32') {
                    assert.ok(basename === 'aspire.exe' || basename === 'aspire.cmd', `Windows path should use aspire.exe or aspire.cmd: ${basename}`);
                }
                else {
                    assert.strictEqual(basename, 'aspire');
                }
            }
        });

        test('includes Windows cmd shim path for dotnet global tools', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const paths = getDefaultCliInstallPaths();
                const homeDir = os.homedir();

                assert.ok(
                    paths.includes(path.join(homeDir, '.dotnet', 'tools', 'aspire.cmd')),
                    'Should check the .cmd shim created by dotnet tool installs on Windows');
            }
            finally {
                platformStub.restore();
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
                findOnPath: async () => 'aspire',
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
                findOnPath: async () => undefined,
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
                findOnPath: async () => undefined,
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
                findOnPath: async () => 'aspire',
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire');
            assert.ok(setConfiguredPath.notCalled, 'should not update settings when CLI is on PATH');
        });

        test('uses concrete Windows PATH executable returned by path discovery', async () => {
            const windowsPathExecutable = 'C:\\Users\\user\\.dotnet\\tools\\aspire.exe';

            const deps = createMockDeps({
                findOnPath: async () => windowsPathExecutable,
                findAtDefaultPath: async () => bundlePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, windowsPathExecutable);
        });

        test('prefers valid configured default path over PATH', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                findOnPath: async () => 'aspire',
                tryExecute: async (p) => p === bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, bundlePath);
            assert.ok(setConfiguredPath.notCalled, 'should not clear an explicit configured path');
        });

        test('prefers configured Windows cmd shim path over PATH', async () => {
            const setConfiguredPath = sinon.stub().resolves();
            const windowsGlobalToolCmdPath = 'C:\\Users\\user\\.dotnet\\tools\\aspire.cmd';

            const deps = createMockDeps({
                getConfiguredPath: () => windowsGlobalToolCmdPath,
                getDefaultPaths: () => [
                    'C:\\Users\\user\\.aspire\\bin\\aspire.exe',
                    'C:\\Users\\user\\.aspire\\bin\\aspire.cmd',
                    'C:\\Users\\user\\.dotnet\\tools\\aspire.exe',
                    windowsGlobalToolCmdPath,
                ],
                findOnPath: async () => 'C:\\Users\\user\\.dotnet\\tools\\aspire.exe',
                tryExecute: async (p) => p === windowsGlobalToolCmdPath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, windowsGlobalToolCmdPath);
            assert.ok(setConfiguredPath.notCalled, 'should not clear an explicit configured .cmd path');
        });

        test('returns not-found when CLI is not on PATH and not at any default path', async () => {
            const deps = createMockDeps({
                findOnPath: async () => undefined,
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

        test('falls through to PATH check when custom configured path is invalid', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                findOnPath: async () => 'aspire',
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.available, true);
        });

        test('falls through to default path when custom configured path is invalid and not on PATH', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.strictEqual(result.cliPath, bundlePath);
            assert.ok(setConfiguredPath.calledOnceWith(bundlePath));
        });

        test('does not update setting when already set to the found default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
                tryExecute: async (p) => p === bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, bundlePath);
            assert.ok(setConfiguredPath.notCalled, 'should not re-set the path if it already matches');
        });
    });

    suite('tryExecuteCli', () => {
        test('routes Windows PATH lookup through cmd.exe', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const result = getCliExecutionCommand('aspire', ['--version']);

                assert.strictEqual(result.file, process.env.ComSpec);
                assert.deepStrictEqual(result.args, ['/d', '/c', 'call', 'aspire', '"--version"']);
                assert.strictEqual(result.windowsVerbatimArguments, true);
            }
            finally {
                platformStub.restore();

                if (originalComSpec === undefined) {
                    delete process.env.ComSpec;
                }
                else {
                    process.env.ComSpec = originalComSpec;
                }
            }
        });

        test('uses direct execution for non-Windows PATH lookup', () => {
            const platformStub = sinon.stub(process, 'platform').value('darwin');

            try {
                const result = getCliExecutionCommand('aspire', ['--version']);

                assert.strictEqual(result.file, 'aspire');
                assert.deepStrictEqual(result.args, ['--version']);
                assert.strictEqual(result.windowsVerbatimArguments, false);
            }
            finally {
                platformStub.restore();
            }
        });

        test('uses direct execution for Windows exe paths', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const result = getCliExecutionCommand('C:\\Users\\user\\.aspire\\bin\\aspire.exe', ['--version']);

                assert.strictEqual(result.file, 'C:\\Users\\user\\.aspire\\bin\\aspire.exe');
                assert.deepStrictEqual(result.args, ['--version']);
                assert.strictEqual(result.windowsVerbatimArguments, false);
            }
            finally {
                platformStub.restore();
            }
        });

        test('routes explicit Windows cmd shim paths with spaces through cmd.exe', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const shimPath = 'C:\\Users\\user\\cli tools & shims\\aspire.cmd';
                const result = getCliExecutionCommand(shimPath, ['--version']);

                assert.strictEqual(result.file, process.env.ComSpec);
                assert.deepStrictEqual(result.args, ['/d', '/c', 'call', `"${shimPath}"`, '"--version"']);
                assert.strictEqual(result.windowsVerbatimArguments, true);
            }
            finally {
                platformStub.restore();

                if (originalComSpec === undefined) {
                    delete process.env.ComSpec;
                }
                else {
                    process.env.ComSpec = originalComSpec;
                }
            }
        });

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
