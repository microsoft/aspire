import * as assert from 'assert';
import * as sinon from 'sinon';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import {
    findCliOnPath,
    getDefaultCliInstallPaths,
    resolveCliPath,
    CliPathDependencies,
    tryExecuteCli,
    CliProbeExecutor,
    isConfiguredCliPathRejectedForForwarding,
    resetRejectedConfiguredCliPathForForwarding,
} from '../utils/cliPath';

const bundlePath = '/home/user/.aspire/bin/aspire';
const globalToolPath = '/home/user/.dotnet/tools/aspire';
const defaultPaths = [bundlePath, globalToolPath];

function createMockDeps(overrides: Partial<CliPathDependencies> = {}): CliPathDependencies {
    return {
        getConfiguredPath: () => '',
        getDefaultPaths: () => defaultPaths,
        isConfiguredPathAutoConfigured: (configuredPath, paths) => paths.some(candidate => process.platform === 'win32'
            ? path.win32.normalize(candidate).toLowerCase() === path.win32.normalize(configuredPath).toLowerCase()
            : candidate === configuredPath),
        findOnPath: async () => undefined,
        findAtDefaultPath: async () => undefined,
        tryExecute: async () => false,
        setConfiguredPath: async () => {},
        updateResolvedPathForForwarding: () => {},
        ...overrides,
    };
}

suite('utils/cliPath tests', () => {
    let originalDotnetCliHome: string | undefined;

    setup(() => {
        originalDotnetCliHome = process.env.DOTNET_CLI_HOME;
        delete process.env.DOTNET_CLI_HOME;
    });

    teardown(() => {
        if (originalDotnetCliHome === undefined) {
            delete process.env.DOTNET_CLI_HOME;
        }
        else {
            process.env.DOTNET_CLI_HOME = originalDotnetCliHome;
        }
    });

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

        test('prefers the Windows global tool executable over its command shim fallback', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const paths = getDefaultCliInstallPaths();

                assert.deepStrictEqual(paths.map(candidate => path.basename(candidate)), [
                    'aspire.exe',
                    'aspire.exe',
                    'aspire.cmd',
                ]);
            }
            finally {
                platformStub.restore();
            }
        });

        test('uses DOTNET_CLI_HOME for global tool candidates without moving the bundle candidate', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalDotNetCliHome = process.env.DOTNET_CLI_HOME;
            const dotnetCliHome = path.join(os.tmpdir(), 'custom-dotnet-cli-home');
            process.env.DOTNET_CLI_HOME = dotnetCliHome;

            try {
                const paths = getDefaultCliInstallPaths();

                assert.strictEqual(paths[0], path.join(os.homedir(), '.aspire', 'bin', 'aspire.exe'));
                assert.deepStrictEqual(paths.slice(1), [
                    path.join(dotnetCliHome, '.dotnet', 'tools', 'aspire.exe'),
                    path.join(dotnetCliHome, '.dotnet', 'tools', 'aspire.cmd'),
                ]);
            }
            finally {
                platformStub.restore();
                if (originalDotNetCliHome === undefined) {
                    delete process.env.DOTNET_CLI_HOME;
                }
                else {
                    process.env.DOTNET_CLI_HOME = originalDotNetCliHome;
                }
            }
        });

        test('uses extensionless executable names outside Windows', () => {
            const platformStub = sinon.stub(process, 'platform').value('linux');

            try {
                process.env.DOTNET_CLI_HOME = path.join(os.tmpdir(), 'ignored-dotnet-cli-home');
                const paths = getDefaultCliInstallPaths();

                for (const p of paths) {
                    assert.strictEqual(path.basename(p), 'aspire');
                }
                assert.ok(paths[1].startsWith(path.join(os.homedir(), '.dotnet', 'tools')));
            }
            finally {
                platformStub.restore();
            }
        });
    });

    suite('findCliOnPath', () => {
        test('returns a concrete Windows command shim from PATH', async () => {
            const commandShim = 'C:\\npm\\aspire.cmd';
            const result = await findCliOnPath({
                platform: 'win32',
                pathValue: 'C:\\npm',
                fileExists: async candidate => candidate === commandShim,
                tryExecute: async candidate => candidate === commandShim,
            });

            assert.strictEqual(result, commandShim);
        });

        test('returns a concrete Windows command shim from a UNC PATH entry', async () => {
            const commandShim = '\\\\server\\share\\tools\\aspire.cmd';
            const result = await findCliOnPath({
                platform: 'win32',
                pathValue: '\\\\server\\share\\tools',
                fileExists: async candidate => candidate === commandShim,
                tryExecute: async candidate => candidate === commandShim,
            });

            assert.strictEqual(result, commandShim);
        });

        test('normalizes a forward-slash UNC PATH entry', async () => {
            const commandShim = '\\\\server\\share\\tools\\aspire.cmd';
            const result = await findCliOnPath({
                platform: 'win32',
                pathValue: '//server/share/tools',
                fileExists: async candidate => candidate === commandShim,
                tryExecute: async candidate => candidate === commandShim,
            });

            assert.strictEqual(result, commandShim);
        });

        test('skips relative Windows PATH entries', async () => {
            const commandShim = 'C:\\npm\\aspire.cmd';
            const result = await findCliOnPath({
                platform: 'win32',
                pathValue: 'tools;\\tools;C:\\npm',
                fileExists: async candidate => candidate === 'tools\\aspire.exe'
                    || candidate === '\\tools\\aspire.exe'
                    || candidate === commandShim,
                tryExecute: async () => true,
            });

            assert.strictEqual(result, commandShim);
        });

        test('executes a Windows command shim discovered only through PATH', async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }

            const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire path&a^b(x)-'));
            try {
                const commandShim = path.join(tempDirectory, 'aspire.cmd');
                fs.writeFileSync(commandShim, '@echo off\r\nif "%~1"=="--version" exit /b 0\r\nexit /b 1\r\n');

                assert.strictEqual(
                    await findCliOnPath({ pathValue: tempDirectory }),
                    commandShim);
            }
            finally {
                fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
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

        test('does not repeat discovery after persisting the default install path', async () => {
            let configuredPath = '';
            let onPathCalls = 0;
            let defaultPathCalls = 0;

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                findOnPath: async () => {
                    onPathCalls++;
                    return undefined;
                },
                findAtDefaultPath: async () => {
                    defaultPathCalls++;
                    return bundlePath;
                },
                setConfiguredPath: async value => {
                    configuredPath = value;
                },
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.cliPath, bundlePath);
            assert.strictEqual(onPathCalls, 1);
            assert.strictEqual(defaultPathCalls, 1);
        });

        test('does not persist an automatically discovered command shim as an explicit setting', async () => {
            const commandShim = 'C:\\Users\\user\\.dotnet\\tools\\aspire.cmd';
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getDefaultPaths: () => [...defaultPaths, commandShim],
                findAtDefaultPath: async () => commandShim,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.deepStrictEqual(result, {
                cliPath: commandShim,
                available: true,
                source: 'default-install',
            });
            assert.ok(setConfiguredPath.notCalled);
        });

        test('does not persist a newly recognized default path without legacy provenance', async () => {
            const redirectedGlobalTool = 'D:\\dotnet-home\\.dotnet\\tools\\aspire.exe';
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                findAtDefaultPath: async () => redirectedGlobalTool,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.cliPath, redirectedGlobalTool);
            assert.strictEqual(result.source, 'default-install');
            assert.ok(setConfiguredPath.notCalled);
        });

        test('clears a superseded legacy setting when redirected discovery selects a command shim', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const legacyGlobalToolPath = 'C:\\Users\\user\\.dotnet\\tools\\aspire.exe';
            const redirectedCommandShim = 'D:\\dotnet-home\\.dotnet\\tools\\aspire.cmd';
            const setConfiguredPath = sinon.stub().resolves();

            try {
                const result = await resolveCliPath(createMockDeps({
                    getConfiguredPath: () => legacyGlobalToolPath,
                    getDefaultPaths: () => [legacyGlobalToolPath],
                    findAtDefaultPath: async () => redirectedCommandShim,
                    setConfiguredPath,
                }));

                assert.deepStrictEqual(result, {
                    cliPath: redirectedCommandShim,
                    available: true,
                    source: 'default-install',
                });
                assert.ok(setConfiguredPath.calledOnceWithExactly(''));
            }
            finally {
                platformStub.restore();
            }
        });

        test('keeps a valid legacy global-tool setting as a final fallback', async () => {
            const legacyGlobalToolPath = path.join(os.homedir(), '.dotnet', 'tools', 'aspire.exe');
            const tryExecute = sinon.stub().callsFake(async candidate => candidate === legacyGlobalToolPath);

            const result = await resolveCliPath(createMockDeps({
                getConfiguredPath: () => legacyGlobalToolPath,
                getDefaultPaths: () => [legacyGlobalToolPath],
                findAtDefaultPath: async () => undefined,
                tryExecute,
            }));

            assert.deepStrictEqual(result, {
                cliPath: legacyGlobalToolPath,
                available: true,
                source: 'default-install',
            });
            assert.ok(tryExecute.calledOnceWithExactly(legacyGlobalToolPath));
        });

        test('treats casing-equivalent Windows legacy paths as auto-configured', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const legacyGlobalToolPath = 'C:\\Users\\User\\.dotnet\\tools\\aspire.exe';
            const configuredPath = 'c:\\users\\user\\.dotnet\\tools\\ASPIRE.EXE';
            const setConfiguredPath = sinon.stub().resolves();
            const tryExecute = sinon.stub().resolves(true);

            try {
                const result = await resolveCliPath(createMockDeps({
                    getConfiguredPath: () => configuredPath,
                    getDefaultPaths: () => [legacyGlobalToolPath],
                    findOnPath: async () => 'aspire',
                    tryExecute,
                    setConfiguredPath,
                }));

                assert.deepStrictEqual(result, { cliPath: 'aspire', available: true, source: 'path' });
                assert.ok(tryExecute.notCalled);
                assert.ok(setConfiguredPath.calledOnceWithExactly(''));
            }
            finally {
                platformStub.restore();
            }
        });

        test('keeps a workspace-scoped legacy path as an explicit user pin', async () => {
            const legacyGlobalToolPath = '/home/user/.dotnet/tools/aspire';
            const findOnPath = sinon.stub().resolves('aspire');
            const tryExecute = sinon.stub().resolves(true);
            const setConfiguredPath = sinon.stub().resolves();

            const result = await resolveCliPath(createMockDeps({
                getConfiguredPath: () => legacyGlobalToolPath,
                getDefaultPaths: () => [legacyGlobalToolPath],
                isConfiguredPathAutoConfigured: () => false,
                findOnPath,
                tryExecute,
                setConfiguredPath,
            }));

            assert.deepStrictEqual(result, {
                cliPath: legacyGlobalToolPath,
                available: true,
                source: 'configured',
            });
            assert.ok(tryExecute.calledOnceWithExactly(legacyGlobalToolPath));
            assert.ok(findOnPath.notCalled);
            assert.ok(setConfiguredPath.notCalled);
        });

        test('keeps syntactically equivalent non-Windows paths explicit', async () => {
            const platformStub = sinon.stub(process, 'platform').value('linux');
            const configuredPath = '/home/user/.dotnet/tools/../tools/aspire';
            const findOnPath = sinon.stub().resolves('aspire');

            try {
                const result = await resolveCliPath(createMockDeps({
                    getConfiguredPath: () => configuredPath,
                    getDefaultPaths: () => [globalToolPath],
                    findOnPath,
                    tryExecute: async candidate => candidate === configuredPath,
                }));

                assert.deepStrictEqual(result, {
                    cliPath: configuredPath,
                    available: true,
                    source: 'configured',
                });
                assert.ok(findOnPath.notCalled);
            }
            finally {
                platformStub.restore();
            }
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

        test('returns a concrete command shim discovered on PATH', async () => {
            const commandShim = 'C:\\npm\\aspire.cmd';
            const deps = createMockDeps({
                findOnPath: async () => commandShim,
            });

            const result = await resolveCliPath(deps);

            assert.deepStrictEqual(result, {
                cliPath: commandShim,
                available: true,
                source: 'path',
            });
        });

        test('publishes a concrete PATH discovery for environment forwarding', async () => {
            const commandShim = 'C:\\npm\\aspire.cmd';
            const updateResolvedPathForForwarding = sinon.stub();
            const deps = createMockDeps({
                findOnPath: async () => commandShim,
                updateResolvedPathForForwarding,
            });

            await resolveCliPath(deps);

            assert.ok(updateResolvedPathForForwarding.calledOnceWithExactly('', commandShim));
        });

        test('clears setting when CLI is on PATH and setting was previously set to a default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                findOnPath: async () => 'aspire',
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
                findOnPath: async () => 'aspire',
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.ok(setConfiguredPath.calledOnceWith(''), 'should clear the setting');
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

        test('keeps an explicitly configured Windows command shim ahead of PATH', async () => {
            const configuredShim = 'C:\\Users\\user\\.dotnet\\tools\\aspire.cmd';
            const findOnPath = sinon.stub().resolves('aspire');

            const deps = createMockDeps({
                getConfiguredPath: () => configuredShim,
                getDefaultPaths: () => [...defaultPaths, configuredShim],
                findOnPath,
                tryExecute: async candidate => candidate === configuredShim,
            });

            const result = await resolveCliPath(deps);

            assert.deepStrictEqual(result, {
                cliPath: configuredShim,
                available: true,
                source: 'configured',
            });
            assert.ok(findOnPath.notCalled);
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

        test('does not overwrite an invalid explicit setting when falling back to a default path', async () => {
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
            assert.ok(setConfiguredPath.notCalled);
        });

        test('does not update setting when already set to the found default path', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => bundlePath,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.ok(setConfiguredPath.notCalled, 'should not re-set the path if it already matches');
        });
    });

    suite('configured path forwarding suppression', () => {
        const configuredPath = '/opt/custom/aspire';
        const discoveredShim = 'C:\\Users\\me\\.dotnet\\tools\\aspire.cmd';

        setup(() => {
            resetRejectedConfiguredCliPathForForwarding();
        });

        teardown(() => {
            resetRejectedConfiguredCliPathForForwarding();
        });

        test('suppresses forwarding when an explicitly configured path fails and discovery falls back', async () => {
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => false,
                findOnPath: async () => undefined,
                findAtDefaultPath: async () => discoveredShim,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.cliPath, discoveredShim);
            assert.strictEqual(result.source, 'default-install');
            assert.ok(
                isConfiguredCliPathRejectedForForwarding(configuredPath),
                'the rejected configured path must not keep forwarding as AspireCliPath');
            assert.ok(setConfiguredPath.notCalled, 'an explicit user setting should be suppressed, not silently erased');
        });

        test('publishes a discovered fallback for environment forwarding', async () => {
            const updateResolvedPathForForwarding = sinon.stub();
            const deps = {
                ...createMockDeps({
                    getConfiguredPath: () => configuredPath,
                    tryExecute: async () => false,
                    findAtDefaultPath: async () => discoveredShim,
                }),
                updateResolvedPathForForwarding,
            } as CliPathDependencies;

            await resolveCliPath(deps);

            assert.ok(updateResolvedPathForForwarding.calledOnceWithExactly(configuredPath, discoveredShim));
        });

        test('suppresses forwarding when an explicitly configured path fails and the CLI is on PATH', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => false,
                findOnPath: async () => 'aspire',
                findAtDefaultPath: async () => undefined,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'path');
            assert.ok(isConfiguredCliPathRejectedForForwarding(configuredPath));
        });

        test('does not suppress forwarding when the configured path executes successfully', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => true,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.cliPath, configuredPath);
            assert.strictEqual(result.source, 'configured');
            assert.strictEqual(isConfiguredCliPathRejectedForForwarding(configuredPath), false);
        });

        test('clears a previous suppression once the configured path starts working', async () => {
            const failing = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => false,
                findAtDefaultPath: async () => discoveredShim,
            });

            await resolveCliPath(failing);
            assert.ok(isConfiguredCliPathRejectedForForwarding(configuredPath));

            const recovered = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => true,
            });

            await resolveCliPath(recovered);
            assert.strictEqual(isConfiguredCliPathRejectedForForwarding(configuredPath), false);
        });

        test('only suppresses the configured path that was actually rejected', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => false,
                findAtDefaultPath: async () => discoveredShim,
            });

            await resolveCliPath(deps);

            assert.strictEqual(isConfiguredCliPathRejectedForForwarding('/some/other/aspire'), false);
        });

        test('does not let an older resolution clear a newer configured-path rejection', async () => {
            const olderConfiguredPath = '/opt/old/aspire';
            const newerConfiguredPath = '/opt/new/aspire';
            let configuredPath = olderConfiguredPath;
            let completeOlderProbe: ((value: boolean) => void) | undefined;
            const olderProbe = new Promise<boolean>(resolve => completeOlderProbe = resolve);

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async candidate => candidate === olderConfiguredPath
                    ? olderProbe
                    : false,
                findOnPath: async () => 'aspire',
            });

            const olderResolution = resolveCliPath(deps);
            configuredPath = newerConfiguredPath;

            const newerResolution = await resolveCliPath(deps);
            assert.strictEqual(newerResolution.source, 'path');
            assert.ok(isConfiguredCliPathRejectedForForwarding(newerConfiguredPath));

            completeOlderProbe!(true);
            await olderResolution;

            assert.ok(
                isConfiguredCliPathRejectedForForwarding(newerConfiguredPath),
                'an older in-flight resolution must not clear suppression for the current setting');
        });

        test('does not publish a stale fallback when the setting returns to the same snapshot', async () => {
            const firstConfiguredPath = '/opt/first/aspire';
            const intermediateConfiguredPath = '/opt/intermediate/aspire';
            const oldFallback = '/opt/old-fallback/aspire';
            const newFallback = '/opt/new-fallback/aspire';
            let configuredPathValue = firstConfiguredPath;
            let completeOldFallback: ((value: string) => void) | undefined;
            let notifyOldFallbackStarted: (() => void) | undefined;
            const oldFallbackStarted = new Promise<void>(resolve => notifyOldFallbackStarted = resolve);
            const oldFallbackPromise = new Promise<string>(resolve => completeOldFallback = resolve);
            const updateResolvedPathForForwarding = sinon.stub();
            let firstFallbackPending = true;

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPathValue,
                findAtDefaultPath: async () => {
                    if (firstFallbackPending) {
                        firstFallbackPending = false;
                        notifyOldFallbackStarted!();
                        return oldFallbackPromise;
                    }

                    return configuredPathValue === intermediateConfiguredPath
                        ? '/opt/intermediate-fallback/aspire'
                        : newFallback;
                },
                updateResolvedPathForForwarding,
            });

            const olderResolution = resolveCliPath(deps);
            await oldFallbackStarted;

            configuredPathValue = intermediateConfiguredPath;
            await resolveCliPath(deps);

            configuredPathValue = firstConfiguredPath;
            await resolveCliPath(deps);

            completeOldFallback!(oldFallback);
            await olderResolution;

            assert.ok(
                updateResolvedPathForForwarding.neverCalledWith(firstConfiguredPath, oldFallback),
                'an older resolution must not publish its fallback after a newer generation restored the same setting');
        });

        test('does not persist a default path from a stale resolution', async () => {
            const intermediateConfiguredPath = '/opt/intermediate/aspire';
            const redirectedFallback = '/opt/redirected/aspire';
            let configuredPathValue = '';
            let completeOldFallback: ((value: string) => void) | undefined;
            let notifyOldFallbackStarted: (() => void) | undefined;
            const oldFallbackStarted = new Promise<void>(resolve => notifyOldFallbackStarted = resolve);
            const oldFallbackPromise = new Promise<string>(resolve => completeOldFallback = resolve);
            const setConfiguredPath = sinon.stub().callsFake(async (value: string) => {
                configuredPathValue = value;
            });
            let firstFallbackPending = true;

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPathValue,
                tryExecute: async () => false,
                findAtDefaultPath: async () => {
                    if (firstFallbackPending) {
                        firstFallbackPending = false;
                        notifyOldFallbackStarted!();
                        return oldFallbackPromise;
                    }

                    return redirectedFallback;
                },
                setConfiguredPath,
            });

            const olderResolution = resolveCliPath(deps);
            await oldFallbackStarted;

            configuredPathValue = intermediateConfiguredPath;
            await resolveCliPath(deps);

            configuredPathValue = '';
            await resolveCliPath(deps);

            completeOldFallback!(bundlePath);
            await olderResolution;

            assert.ok(
                setConfiguredPath.neverCalledWith(bundlePath),
                'an older resolution must not persist its default path after a newer generation restored the same setting');
        });

        test('shares concurrent resolutions for the same configured path', async () => {
            let completeProbe: ((value: boolean) => void) | undefined;
            const probe = new Promise<boolean>(resolve => completeProbe = resolve);
            let probeCount = 0;

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPath,
                tryExecute: async () => {
                    probeCount++;
                    return probe;
                },
            });

            const firstResolution = resolveCliPath(deps);
            const secondResolution = resolveCliPath(deps);

            await Promise.resolve();
            assert.strictEqual(probeCount, 1, 'concurrent callers should share one CLI probe');

            completeProbe!(true);
            const [firstResult, secondResult] = await Promise.all([firstResolution, secondResolution]);

            assert.deepStrictEqual(firstResult, secondResult);
            assert.strictEqual(firstResult.cliPath, configuredPath);
        });

        test('restarts resolution when the configured path scope changes', async () => {
            let isAutoConfigured = true;
            let completePathProbe: ((value: string) => void) | undefined;
            const pathProbe = new Promise<string>(resolve => completePathProbe = resolve);
            const tryExecute = sinon.stub().resolves(true);
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                getConfiguredPath: () => globalToolPath,
                isConfiguredPathAutoConfigured: () => isAutoConfigured,
                findOnPath: async () => pathProbe,
                tryExecute,
                setConfiguredPath,
            });

            const autoConfiguredResolution = resolveCliPath(deps);
            isAutoConfigured = false;
            const explicitResolution = resolveCliPath(deps);
            completePathProbe!('aspire');

            const [autoConfiguredResult, explicitResult] = await Promise.all([
                autoConfiguredResolution,
                explicitResolution,
            ]);

            assert.strictEqual(autoConfiguredResult.source, 'configured');
            assert.strictEqual(explicitResult.source, 'configured');
            assert.ok(tryExecute.called, 'the workspace-scoped path should be probed as an explicit user pin');
            assert.ok(setConfiguredPath.notCalled, 'the stale auto-configured resolution must not update the global setting');
        });

        test('retries a resolution when the configured path changes during its probe', async () => {
            const olderConfiguredPath = '/opt/old/aspire';
            const newerConfiguredPath = '/opt/new/aspire';
            let configuredPathValue = olderConfiguredPath;
            let completeOlderProbe: ((value: boolean) => void) | undefined;
            const olderProbe = new Promise<boolean>(resolve => completeOlderProbe = resolve);

            const deps = createMockDeps({
                getConfiguredPath: () => configuredPathValue,
                tryExecute: async candidate => candidate === olderConfiguredPath
                    ? olderProbe
                    : true,
            });

            const resolution = resolveCliPath(deps);
            configuredPathValue = newerConfiguredPath;
            completeOlderProbe!(true);

            const result = await resolution;

            assert.strictEqual(result.cliPath, newerConfiguredPath);
            assert.strictEqual(result.source, 'configured');
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

        test('validates Windows cmd wrappers whose path contains cmd.exe metacharacters', async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }

            // '&', '^' and parentheses terminate or regroup an unquoted cmd.exe command line, so a
            // shim under such a directory is only reachable through the quoted wrapper. Directory
            // names with no space are the failing shape, because libuv only auto-quotes arguments
            // containing a space, tab, or quote. An unmatched '%' remains literal; paired `%NAME%`
            // references are expanded by cmd.exe and cannot be preserved by this wrapper.
            const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire%literal&cli^test(x)-'));
            try {
                const wrapperPath = path.join(tempDirectory, 'aspire.cmd');
                fs.writeFileSync(wrapperPath, '@echo off\r\nif "%~1"=="--version" (\r\n  echo 13.5.0-pr.e2e\r\n  exit /b 0\r\n)\r\nexit /b 1\r\n');

                assert.strictEqual(await tryExecuteCli(wrapperPath), true);
            }
            finally {
                fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
            }
        });

        test('probes Windows command shims through the quoted verbatim cmd.exe wrapper', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const originalComSpec = process.env.ComSpec;
            process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

            try {
                const calls: Array<{ command: string; args: string[]; timeout: number; windowsVerbatimArguments?: boolean }> = [];
                const execute: CliProbeExecutor = async (command, args, options) => {
                    calls.push({ command, args, timeout: options.timeout, windowsVerbatimArguments: options.windowsVerbatimArguments });
                };

                const result = await tryExecuteCli('C:\\Users\\a&b\\.dotnet\\tools\\aspire.cmd', execute);

                assert.strictEqual(result, true);
                assert.deepStrictEqual(calls, [{
                    command: 'C:\\Windows\\System32\\cmd.exe',
                    args: ['/d', '/v:off', '/s', '/c', '""C:\\Users\\a&b\\.dotnet\\tools\\aspire.cmd" "--version""'],
                    timeout: 5000,
                    windowsVerbatimArguments: true,
                }]);
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

        test('probes native executables directly without a cmd.exe wrapper', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const calls: Array<{ command: string; args: string[]; windowsVerbatimArguments?: boolean }> = [];
                const execute: CliProbeExecutor = async (command, args, options) => {
                    calls.push({ command, args, windowsVerbatimArguments: options.windowsVerbatimArguments });
                };

                assert.strictEqual(await tryExecuteCli('C:\\Users\\me\\.aspire\\bin\\aspire.exe', execute), true);
                assert.deepStrictEqual(calls, [{
                    command: 'C:\\Users\\me\\.aspire\\bin\\aspire.exe',
                    args: ['--version'],
                    windowsVerbatimArguments: undefined,
                }]);
            }
            finally {
                platformStub.restore();
            }
        });

        test('rejects shim paths containing terminal control characters', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');

            try {
                const execute: CliProbeExecutor = async () => {
                    assert.fail('should not execute a shim path containing control characters');
                };

                assert.strictEqual(await tryExecuteCli('C:\\tools\\asp\r\nire.cmd', execute), false);
            }
            finally {
                platformStub.restore();
            }
        });
    });
});
