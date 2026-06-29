import * as assert from 'assert';
import * as sinon from 'sinon';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { getDefaultCliInstallPaths, getWindowsPathCliCandidates, resolveCliPath, CliPathDependencies, tryExecuteCli } from '../utils/cliPath';
import { getCliExecutionCommand } from '../utils/cliExecution';

const bundlePath = '/home/user/.aspire/bin/aspire';
const globalToolPath = '/home/user/.dotnet/tools/aspire';
const execFileAsync = promisify(execFile);

function createMockDeps(overrides: Partial<CliPathDependencies> = {}): CliPathDependencies {
    return {
        getConfiguredPath: () => '',
        findOnPath: async () => undefined,
        findAtDefaultPath: async () => undefined,
        tryExecute: async () => false,
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

            const deps = createMockDeps({
                getConfiguredPath: () => '/configured/path/aspire',
                findOnPath: async () => 'aspire',
                tryExecute: async (p) => p === e2ePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, e2ePath);
        });

        test('falls back to default install path when CLI is not on PATH', async () => {
            const deps = createMockDeps({
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'default-install');
            assert.strictEqual(result.cliPath, bundlePath);
        });

        test('resolves default install path without a settings writer', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => '',
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.strictEqual(result.cliPath, bundlePath);
        });

        test('prefers PATH over default install path', async () => {
            const deps = createMockDeps({
                findOnPath: async () => 'aspire',
                findAtDefaultPath: async () => bundlePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.available, true);
            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire');
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

        test('prefers PATH over configured default path', async () => {
            const defaultPath = getDefaultCliInstallPaths()[0];
            const deps = createMockDeps({
                getConfiguredPath: () => defaultPath,
                findOnPath: async () => 'aspire',
                tryExecute: async (p) => p === defaultPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.strictEqual(result.cliPath, 'aspire');
        });

        test('prefers PATH over configured Windows default path with alternate separators', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const windowsGlobalToolCmdPath = getDefaultCliInstallPaths()
                    .find(p => p.endsWith('aspire.cmd'))!
                    .replaceAll('/', '\\');
                const deps = createMockDeps({
                    getConfiguredPath: () => windowsGlobalToolCmdPath,
                    findOnPath: async () => 'C:\\Tools\\aspire.exe',
                    tryExecute: async (p) => p === windowsGlobalToolCmdPath,
                });

                const result = await resolveCliPath(deps);

                assert.strictEqual(result.source, 'path');
                assert.strictEqual(result.cliPath, 'C:\\Tools\\aspire.exe');
            }
            finally {
                platformStub.restore();
            }
        });

        test('prefers configured custom Windows cmd shim path over PATH', async () => {
            const windowsGlobalToolCmdPath = 'D:\\Cli Shims\\aspire.cmd';

            const deps = createMockDeps({
                getConfiguredPath: () => windowsGlobalToolCmdPath,
                findOnPath: async () => 'C:\\Users\\user\\.dotnet\\tools\\aspire.exe',
                tryExecute: async (p) => p === windowsGlobalToolCmdPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, windowsGlobalToolCmdPath);
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
            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.strictEqual(result.cliPath, bundlePath);
        });

        test('uses configured default path when it is valid', async () => {
            const defaultPath = getDefaultCliInstallPaths()[0];
            const deps = createMockDeps({
                getConfiguredPath: () => defaultPath,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
                tryExecute: async (p) => p === defaultPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(result.cliPath, defaultPath);
        });
    });

    suite('tryExecuteCli', () => {
        test('builds Windows PATH candidates without current directory entries', () => {
            const candidates = getWindowsPathCliCandidates({
                Path: '.;.\\tools;tools;C:relative;C:\\Tools; ;C:\\Users\\user\\.dotnet\\tools;',
                PATHEXT: '.EXE;.CMD',
            });

            assert.deepStrictEqual(candidates, [
                'C:\\Tools\\aspire.EXE',
                'C:\\Tools\\aspire.CMD',
                'C:\\Users\\user\\.dotnet\\tools\\aspire.EXE',
                'C:\\Users\\user\\.dotnet\\tools\\aspire.CMD',
            ]);
        });

        test('uses direct execution for unresolved bare Windows command', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const result = getCliExecutionCommand('aspire', ['--version']);

                assert.strictEqual(result.file, 'aspire');
                assert.deepStrictEqual(result.args, ['--version']);
                assert.strictEqual(result.windowsVerbatimArguments, false);
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
                assert.deepStrictEqual(result.args, ['/d', '/v:off', '/s', '/c', 'call "C:\\Users\\user\\cli tools & shims\\aspire.cmd" "--version"']);
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

        test('escapes percent signs for Windows cmd shim arguments across call reparsing', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const result = getCliExecutionCommand('C:\\Tools\\aspire.cmd', ['--source', '%PRIVATE_FEED%']);

                assert.strictEqual(result.file, process.env.ComSpec);
                assert.deepStrictEqual(result.args, ['/d', '/v:off', '/s', '/c', 'call "C:\\Tools\\aspire.cmd" "--source" "%%%%PRIVATE_FEED%%%%"']);
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

        test('escapes trailing backslashes for Windows cmd shim arguments', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const result = getCliExecutionCommand('C:\\Tools\\aspire.cmd', ['--path=C:\\out\\']);

                assert.strictEqual(result.file, process.env.ComSpec);
                assert.deepStrictEqual(result.args, ['/d', '/v:off', '/s', '/c', String.raw`call "C:\Tools\aspire.cmd" "--path=C:\out\\"`]);
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

        test('passes literal percent-delimited values through Windows cmd wrappers', async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }

            const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-path-percent-test-'));
            try {
                const wrapperPath = path.join(tempDirectory, 'aspire.cmd');
                const scriptPath = path.join(tempDirectory, 'assert-percent.js');
                fs.writeFileSync(scriptPath, `const actual = process.argv[2];\nif (actual !== '%PRIVATE_FEED%') {\n  console.error(JSON.stringify(process.argv.slice(2)));\n  process.exit(1);\n}\n`);
                fs.writeFileSync(wrapperPath, `@echo off\r\n"${process.execPath}" "${scriptPath}" %*\r\n`);

                const command = getCliExecutionCommand(wrapperPath, ['%PRIVATE_FEED%']);
                await execFileAsync(command.file, command.args, {
                    env: { ...process.env, PRIVATE_FEED: 'expanded-value' },
                    windowsVerbatimArguments: command.windowsVerbatimArguments,
                });
            }
            finally {
                fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
            }
        });

        test('passes trailing backslash values through Windows cmd wrappers', async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }

            const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-path-backslash-test-'));
            try {
                const wrapperPath = path.join(tempDirectory, 'aspire.cmd');
                const scriptPath = path.join(tempDirectory, 'assert-backslash.js');
                fs.writeFileSync(scriptPath, `const actual = process.argv[2];\nif (actual !== 'C:\\\\out\\\\') {\n  console.error(JSON.stringify(process.argv.slice(2)));\n  process.exit(1);\n}\n`);
                fs.writeFileSync(wrapperPath, `@echo off\r\n"${process.execPath}" "${scriptPath}" %*\r\n`);

                const command = getCliExecutionCommand(wrapperPath, ['C:\\out\\']);
                await execFileAsync(command.file, command.args, {
                    windowsVerbatimArguments: command.windowsVerbatimArguments,
                });
            }
            finally {
                fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
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
