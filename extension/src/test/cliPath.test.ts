import * as assert from 'assert';
import * as sinon from 'sinon';
import * as os from 'os';
import * as path from 'path';
import { getDefaultCliInstallPaths, resolveCliPath, clearCliPathCache, CliPathDependencies } from '../utils/cliPath';

const bundlePath = '/home/user/.aspire/bin/aspire';
const globalToolPath = '/home/user/.dotnet/tools/aspire';
const defaultPaths = [bundlePath, globalToolPath];

function createMockDeps(overrides: Partial<CliPathDependencies> = {}): CliPathDependencies {
    return {
        getConfiguredPath: () => '',
        getDefaultPaths: () => defaultPaths,
        isOnPath: async () => false,
        findAtDefaultPath: async () => undefined,
        tryExecute: async () => false,
        setConfiguredPath: async () => {},
        ...overrides,
    };
}

suite('utils/cliPath tests', () => {
    teardown(() => {
        clearCliPathCache();
        sinon.restore();
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

        test('falls through to PATH check when custom configured path is invalid', async () => {
            const deps = createMockDeps({
                getConfiguredPath: () => '/bad/path/aspire',
                tryExecute: async () => false,
                isOnPath: async () => true,
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
                isOnPath: async () => false,
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
                isOnPath: async () => false,
                findAtDefaultPath: async () => bundlePath,
                setConfiguredPath,
            });

            const result = await resolveCliPath(deps);

            assert.strictEqual(result.source, 'default-install');
            assert.ok(setConfiguredPath.notCalled, 'should not re-set the path if it already matches');
        });

        test('clearCliPathCache does not throw', () => {
            clearCliPathCache();
        });

        test('coalesces concurrent resolutions for the same dependency set', async () => {
            let releaseIsOnPath: (() => void) | undefined;
            let signalIsOnPathStarted: (() => void) | undefined;
            const isOnPathStarted = new Promise<void>(resolve => {
                signalIsOnPathStarted = resolve;
            });

            const isOnPath = sinon.stub().callsFake(async () => {
                signalIsOnPathStarted?.();
                await new Promise<void>(resolveRelease => {
                    releaseIsOnPath = resolveRelease;
                });
                return true;
            });

            const deps = createMockDeps({
                isOnPath,
            });

            const firstResolution = resolveCliPath(deps);
            await isOnPathStarted;
            const secondResolution = resolveCliPath(deps);

            assert.strictEqual(isOnPath.callCount, 1, 'should only run PATH probe once');
            assert.ok(releaseIsOnPath, 'expected first resolution to be in progress');

            releaseIsOnPath();

            const [firstResult, secondResult] = await Promise.all([firstResolution, secondResolution]);
            assert.deepStrictEqual(firstResult, secondResult);
        });

        test('caches successful resolutions for the same dependency set', async () => {
            const isOnPath = sinon.stub().resolves(true);
            const deps = createMockDeps({
                isOnPath,
            });

            const firstResult = await resolveCliPath(deps);
            const secondResult = await resolveCliPath(deps);

            assert.deepStrictEqual(firstResult, secondResult);
            assert.strictEqual(isOnPath.callCount, 1, 'should reuse the cached CLI path resolution');
        });

        test('does not cache unavailable resolutions', async () => {
            const isOnPath = sinon.stub();
            isOnPath.onFirstCall().resolves(false);
            isOnPath.onSecondCall().resolves(true);

            const deps = createMockDeps({
                isOnPath,
                findAtDefaultPath: async () => undefined,
            });

            const unavailableResult = await resolveCliPath(deps);
            const availableResult = await resolveCliPath(deps);

            assert.strictEqual(unavailableResult.available, false);
            assert.strictEqual(availableResult.available, true);
            assert.strictEqual(availableResult.source, 'path');
            assert.strictEqual(isOnPath.callCount, 2, 'should re-probe when the previous resolution was unavailable');
        });

        test('clearCliPathCache invalidates cached resolutions', async () => {
            const isOnPath = sinon.stub();
            isOnPath.onFirstCall().resolves(true);
            isOnPath.onSecondCall().resolves(false);
            const findAtDefaultPath = sinon.stub().resolves(bundlePath);

            const deps = createMockDeps({
                isOnPath,
                findAtDefaultPath,
            });

            const firstResult = await resolveCliPath(deps);
            const cachedResult = await resolveCliPath(deps);

            assert.strictEqual(firstResult.source, 'path');
            assert.strictEqual(cachedResult.source, 'path');
            assert.strictEqual(isOnPath.callCount, 1, 'should use the cached result before invalidation');

            clearCliPathCache();

            const refreshedResult = await resolveCliPath(deps);

            assert.strictEqual(refreshedResult.source, 'default-install');
            assert.strictEqual(refreshedResult.cliPath, bundlePath);
            assert.strictEqual(isOnPath.callCount, 2, 'should re-probe after cache invalidation');
            assert.ok(findAtDefaultPath.calledOnce, 'should probe default paths after cache invalidation');
        });

        test('clearCliPathCache prevents stale in-flight resolutions from updating configuration', async () => {
            let releaseFindAtDefaultPath: (() => void) | undefined;
            let signalFindAtDefaultPathStarted: (() => void) | undefined;
            const findAtDefaultPathStarted = new Promise<void>(resolve => {
                signalFindAtDefaultPathStarted = resolve;
            });
            const setConfiguredPath = sinon.stub().resolves();

            const deps = createMockDeps({
                isOnPath: async () => false,
                findAtDefaultPath: async () => {
                    signalFindAtDefaultPathStarted?.();
                    await new Promise<void>(resolveRelease => {
                        releaseFindAtDefaultPath = resolveRelease;
                    });
                    return bundlePath;
                },
                setConfiguredPath,
            });

            const resolution = resolveCliPath(deps);
            await findAtDefaultPathStarted;

            clearCliPathCache();
            assert.ok(releaseFindAtDefaultPath, 'expected CLI path resolution to be in progress');
            releaseFindAtDefaultPath();

            const result = await resolution;

            assert.strictEqual(result.source, 'default-install');
            assert.ok(setConfiguredPath.notCalled, 'should not update configuration from a stale resolution after cache invalidation');
        });
    });
});
