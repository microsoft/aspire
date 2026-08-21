import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as path from 'path';
import { createWorkspaceFolder } from './testHelpers';
import {
    ASPIRE_CLI_PATH_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR,
    CliPathEnvironmentCollection,
    CliPathEnvironmentDependencies,
    ResolvedCliPathDependencies,
    createAspireCliPathProcessEnvironment,
    getAspireExtensionEnvironment,
    createResolvedAspireCliPathProcessEnvironment,
    CliPathEnvironmentSynchronizer,
    CliPathEnvironmentSynchronizerDependencies,
    getForwardableAspireCliPath,
    getForwardableResolvedAspireCliPath,
    initializeCliPathEnvironmentSync,
    registerCliPathEnvironmentSync,
    syncAspireCliPathEnvironment,
    syncAspireExtensionEnvironment,
} from '../utils/cliPathEnvironment';
import {
    CliPathDependencies,
    CliPathResolver,
    isConfiguredCliPathRejectedForForwarding,
    resetRejectedConfiguredCliPathForForwarding,
    resolveCliPath,
} from '../utils/cliPath';
import { CliPathResolutionTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

function createFakeCollection(): CliPathEnvironmentCollection & { entries: Map<string, string> } {
    const entries = new Map<string, string>();
    return {
        entries,
        description: undefined,
        replace(variable, value) {
            entries.set(variable, value);
        },
        delete(variable) {
            entries.delete(variable);
        },
    };
}

function withPackagePreReleaseMarker(value: string, action: () => void): void {
    const originalValue = process.env.ASPIRE_VSCODE_EXTENSION_PACKAGE_PRERELEASE;
    process.env.ASPIRE_VSCODE_EXTENSION_PACKAGE_PRERELEASE = value;
    try {
        action();
    }
    finally {
        if (originalValue === undefined) {
            delete process.env.ASPIRE_VSCODE_EXTENSION_PACKAGE_PRERELEASE;
        }
        else {
            process.env.ASPIRE_VSCODE_EXTENSION_PACKAGE_PRERELEASE = originalValue;
        }
    }
}

function makeDeps(overrides: Partial<CliPathEnvironmentDependencies> = {}): CliPathEnvironmentDependencies {
    return {
        getConfiguredPath: () => '',
        getResolvedPath: () => undefined,
        isAbsolute: (cliPath: string) => cliPath.startsWith('/') || /^[A-Za-z]:[\\/]/.test(cliPath),
        fileExists: (cliPath: string) => cliPath.endsWith('/aspire') || cliPath.endsWith('\\aspire.exe') || cliPath.endsWith('/aspire.exe'),
        realpath: (cliPath: string) => cliPath,
        isRejectedForForwarding: () => false,
        log: () => { },
        ...overrides,
    };
}

function normalizeCandidate(candidate: string): string {
    return candidate.replace(/\\/g, '/');
}

suite('cliPathEnvironment.syncAspireExtensionEnvironment tests', () => {
    test('contributes stable Marketplace metadata when the pre-release flag is absent', () => {
        withPackagePreReleaseMarker('false', () => {
            const collection = createFakeCollection();

            syncAspireExtensionEnvironment(collection, getAspireExtensionEnvironment({
                version: '1.16.0',
            }, {
                appName: 'Visual Studio Code',
                uriScheme: 'vscode',
            }));

            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR), '1.16.0');
            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR), 'stable');
            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR), 'microsoft-marketplace');
        });
    });

    test('contributes the production Marketplace pre-release metadata', () => {
        withPackagePreReleaseMarker('false', () => {
            const collection = createFakeCollection();

            syncAspireExtensionEnvironment(collection, getAspireExtensionEnvironment({
                version: '1.17.0',
                preRelease: true,
            }, {
                appName: 'Visual Studio Code',
                uriScheme: 'vscode',
            }));

            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR), '1.17.0');
            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR), 'prerelease');
        });
    });

    test('contributes the package-time pre-release marker for an offline VSIX install', () => {
        withPackagePreReleaseMarker('true', () => {
            const collection = createFakeCollection();

            syncAspireExtensionEnvironment(collection, getAspireExtensionEnvironment({
                version: '1.17.0',
                preRelease: false,
            }, {
                appName: 'Visual Studio Code',
                uriScheme: 'vscode',
            }));

            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR), '1.17.0');
            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR), 'prerelease');
        });
    });

    test('contributes the stable channel when no pre-release signal is present', () => {
        withPackagePreReleaseMarker('false', () => {
            const collection = createFakeCollection();

            syncAspireExtensionEnvironment(collection, getAspireExtensionEnvironment({
                version: '1.17.0',
                preRelease: false,
            }, {
                appName: 'Visual Studio Code',
                uriScheme: 'vscode',
            }));

            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR), '1.17.0');
            assert.strictEqual(collection.entries.get(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR), 'stable');
        });
    });

    test('clears stale extension signals when the manifest version is unavailable', () => {
        const collection = createFakeCollection();
        collection.entries.set(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR, '1.16.0');
        collection.entries.set(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR, 'stable');
        collection.entries.set(ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR, 'microsoft-marketplace');

        syncAspireExtensionEnvironment(collection, undefined);

        assert.strictEqual(collection.entries.has(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR), false);
        assert.strictEqual(collection.entries.has(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR), false);
        assert.strictEqual(collection.entries.has(ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR), false);
    });

    test('marks non-Microsoft editor products as another extension source', () => {
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.16.0',
        }, {
            appName: 'VSCodium',
            uriScheme: 'vscodium',
        });

        assert.strictEqual(extensionEnvironment?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'other');
    });

    test('marks official VS Code with a configured gallery as another extension source', () => {
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.16.0',
        }, {
            appName: 'Visual Studio Code',
            uriScheme: 'vscode',
            extensionGalleryServiceUrl: 'https://marketplace.example.test/manifest',
        });

        assert.strictEqual(extensionEnvironment?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'other');
    });

    test('marks official VS Code Insiders with a configured gallery as another extension source', () => {
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.16.0',
        }, {
            appName: 'Visual Studio Code - Insiders',
            uriScheme: 'vscode-insiders',
            extensionGalleryServiceUrl: 'https://marketplace.example.test/manifest',
        });

        assert.strictEqual(extensionEnvironment?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'other');
    });

    test('marks whitespace in the configured gallery setting as another extension source', () => {
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.16.0',
        }, {
            appName: 'Visual Studio Code',
            uriScheme: 'vscode',
            extensionGalleryServiceUrl: '   ',
        });

        assert.strictEqual(extensionEnvironment?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'other');
    });

    test('marks truthy malformed gallery settings as another extension source', () => {
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.16.0',
        }, {
            appName: 'Visual Studio Code',
            uriScheme: 'vscode',
            extensionGalleryServiceUrl: { serviceUrl: 'malformed' },
        });

        assert.strictEqual(extensionEnvironment?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'other');
    });
});

suite('cliPathEnvironment.getForwardableAspireCliPath tests', () => {
    test('returns the configured path when it is absolute and exists', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
        })), '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
    });

    test('returns undefined when the configured path is a bare command name', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => 'aspire',
        })), undefined);
    });

    test('returns undefined when the configured absolute path does not exist', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/missing/aspire',
            fileExists: () => false,
        })), undefined);
    });

    test('returns undefined when the configured path is an unbundled framework-dependent CLI build', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire'
                    || normalized === '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll';
            },
        })), undefined);
    });

    test('returns undefined when the configured path resolves to an unbundled framework-dependent CLI build', () => {
        const symlinkPath = '/Users/me/bin/aspire-dev';
        const repoCliPath = '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire';

        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => symlinkPath,
            realpath: (candidate) => candidate === symlinkPath ? repoCliPath : candidate,
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === symlinkPath
                    || normalized === repoCliPath
                    || normalized === '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll';
            },
        })), undefined);
    });

    test('returns the configured path for a framework-dependent CLI with an install sidecar', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/bin/aspire',
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === '/work/aspire/bin/aspire'
                    || normalized === '/work/aspire/bin/aspire.dll'
                    || normalized === '/work/aspire/bin/.aspire-install.json';
            },
        })), '/work/aspire/bin/aspire');
    });

    test('returns the configured path for a framework-dependent CLI with an adjacent bundle layout', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/bin/aspire',
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === '/work/aspire/bin/aspire'
                    || normalized === '/work/aspire/bin/aspire.dll'
                    || normalized === '/work/aspire/bin/dcp/dcp'
                    || normalized === '/work/aspire/bin/managed/aspire-managed';
            },
        })), '/work/aspire/bin/aspire');
    });

    test('returns undefined when CLI resolution rejected the configured path and fell back elsewhere', () => {
        // resolveCliPath executes a different CLI in this state, so forwarding the configured
        // path would make ResolveAspireCliBundle stamp bundle paths from a CLI that never ran.
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/bin/aspire',
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === '/work/aspire/bin/aspire'
                    || normalized === '/work/aspire/bin/.aspire-install.json';
            },
            isRejectedForForwarding: (candidate) => candidate === '/work/aspire/bin/aspire',
        })), undefined);
    });

    test('returns the effective fallback when the configured path was rejected', () => {
        const deps = {
            ...makeDeps({
                getConfiguredPath: () => '/invalid/aspire',
                isRejectedForForwarding: candidate => candidate === '/invalid/aspire',
            }),
            getResolvedPath: () => '/redirected/aspire',
        };

        assert.strictEqual(getForwardableAspireCliPath(deps), '/redirected/aspire');
    });

    test('keeps forwarding a configured path that resolution did not reject', () => {
        assert.strictEqual(getForwardableAspireCliPath(makeDeps({
            getConfiguredPath: () => '/work/aspire/bin/aspire',
            fileExists: (candidate) => {
                const normalized = normalizeCandidate(candidate);
                return normalized === '/work/aspire/bin/aspire'
                    || normalized === '/work/aspire/bin/.aspire-install.json';
            },
            isRejectedForForwarding: (candidate) => candidate === '/some/other/aspire',
        })), '/work/aspire/bin/aspire');
    });
});

function makeResolvedDeps(overrides: Partial<ResolvedCliPathDependencies> = {}): ResolvedCliPathDependencies {
    return {
        isAbsolute: (cliPath: string) => cliPath.startsWith('/') || /^[A-Za-z]:[\\/]/.test(cliPath),
        fileExists: (cliPath: string) => cliPath.endsWith('/aspire') || cliPath.endsWith('\\aspire.exe') || cliPath.endsWith('/aspire.exe'),
        realpath: (cliPath: string) => cliPath,
        ...overrides,
    };
}

suite('cliPathEnvironment.getForwardableResolvedAspireCliPath tests', () => {
    test('returns the supplied concrete path when it is absolute and exists', () => {
        assert.strictEqual(
            getForwardableResolvedAspireCliPath('/repo/a/bin/aspire', makeResolvedDeps()),
            '/repo/a/bin/aspire');
    });

    test('returns undefined for a bare command name', () => {
        assert.strictEqual(getForwardableResolvedAspireCliPath('aspire', makeResolvedDeps()), undefined);
    });

    test('returns undefined for a missing absolute path', () => {
        assert.strictEqual(
            getForwardableResolvedAspireCliPath('/repo/a/bin/missing-aspire', makeResolvedDeps({ fileExists: () => false })),
            undefined);
    });

    test('returns undefined for undefined input', () => {
        assert.strictEqual(getForwardableResolvedAspireCliPath(undefined, makeResolvedDeps()), undefined);
    });

    test('ignores configured-path rejection state for a concrete path the resolver already launched', () => {
        // The resolver already selected and validated this exact executable, so raw-setting
        // rejection state (which describes a different configured/target value) must not
        // suppress forwarding it.
        const cliPath = '/repo/a/bin/aspire';
        const deps: ResolvedCliPathDependencies & { isRejectedForForwarding: () => boolean } = {
            ...makeResolvedDeps(),
            isRejectedForForwarding: () => true,
        };

        assert.strictEqual(getForwardableResolvedAspireCliPath(cliPath, deps), cliPath);
    });

    test('returns undefined for a resolved unbundled framework-dependent CLI build', () => {
        assert.strictEqual(
            getForwardableResolvedAspireCliPath('/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire', makeResolvedDeps({
                fileExists: (candidate) => {
                    const normalized = normalizeCandidate(candidate);
                    return normalized === '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire'
                        || normalized === '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll';
                },
            })),
            undefined);
    });
});

suite('cliPathEnvironment.createAspireCliPathProcessEnvironment tests', () => {
    test('overlays AspireCliPath for direct extension-owned child processes', () => {
        const env = createAspireCliPathProcessEnvironment(
            { PATH: '/usr/bin', AspireCliPath: '/old/aspire' },
            makeDeps({ getConfiguredPath: () => '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire' }),
        );

        assert.deepStrictEqual(env, {
            PATH: '/usr/bin',
            AspireCliPath: '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
        });
    });

    test('leaves the process environment unchanged when no configured path can be forwarded', () => {
        const baseEnv = { PATH: '/usr/bin', AspireCliPath: '/ambient/aspire' };
        const env = createAspireCliPathProcessEnvironment(
            baseEnv,
            makeDeps({ getConfiguredPath: () => 'aspire' }),
        );

        assert.strictEqual(env, baseEnv);
    });
});

suite('cliPathEnvironment.createResolvedAspireCliPathProcessEnvironment tests', () => {
    teardown(() => sinon.restore());

    test('sets AspireCliPath to the supplied valid concrete path', () => {
        const env = createResolvedAspireCliPathProcessEnvironment(
            '/repo/a/bin/aspire',
            { PATH: '/usr/bin', AspireCliPath: '/old/aspire' },
            makeResolvedDeps());

        assert.deepStrictEqual(env, {
            PATH: '/usr/bin',
            AspireCliPath: '/repo/a/bin/aspire',
        });
    });

    test('removes a stale AspireCliPath when the resolved command is relative', () => {
        const env = createResolvedAspireCliPathProcessEnvironment(
            'aspire',
            { PATH: '/usr/bin', AspireCliPath: '/old/aspire' },
            makeResolvedDeps());

        assert.deepStrictEqual(env, { PATH: '/usr/bin' });
    });

    test('removes a stale AspireCliPath when the resolved executable is missing', () => {
        const env = createResolvedAspireCliPathProcessEnvironment(
            '/repo/a/bin/missing-aspire',
            { PATH: '/usr/bin', AspireCliPath: '/old/aspire' },
            makeResolvedDeps({ fileExists: () => false }));

        assert.deepStrictEqual(env, { PATH: '/usr/bin' });
    });

    test('replaces Windows casing variants with the canonical AspireCliPath key', () => {
        sinon.stub(process, 'platform').value('win32');

        const env = createResolvedAspireCliPathProcessEnvironment(
            'C:\\repo\\a\\bin\\aspire.exe',
            { PATH: 'C:\\Windows', ASPIRECLIPATH: 'C:\\old\\aspire.exe' },
            makeResolvedDeps());

        assert.deepStrictEqual(env, {
            PATH: 'C:\\Windows',
            AspireCliPath: 'C:\\repo\\a\\bin\\aspire.exe',
        });
    });
});

suite('cliPathEnvironment.syncAspireCliPathEnvironment tests', () => {
    test('sets AspireCliPath when the configured path is an absolute Unix path', () => {
        const collection = createFakeCollection();

        const applied = syncAspireCliPathEnvironment(collection, makeDeps({
            getConfiguredPath: () => '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
        }));

        assert.strictEqual(applied, '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/work/aspire/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
    });

    test('sets AspireCliPath when the configured path is an absolute Windows path', () => {
        const collection = createFakeCollection();

        const applied = syncAspireCliPathEnvironment(collection, makeDeps({
            getConfiguredPath: () => 'C:\\src\\aspire\\artifacts\\bin\\Aspire.Cli\\Debug\\net10.0\\aspire.exe',
        }));

        assert.strictEqual(applied, 'C:\\src\\aspire\\artifacts\\bin\\Aspire.Cli\\Debug\\net10.0\\aspire.exe');
        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), 'C:\\src\\aspire\\artifacts\\bin\\Aspire.Cli\\Debug\\net10.0\\aspire.exe');
    });

    test('clears AspireCliPath when the configured path is empty', () => {
        const collection = createFakeCollection();
        collection.entries.set(ASPIRE_CLI_PATH_ENV_VAR, '/stale/aspire');

        const applied = syncAspireCliPathEnvironment(collection, makeDeps({ getConfiguredPath: () => '' }));

        assert.strictEqual(applied, undefined);
        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
    });

    test('clears AspireCliPath when the configured path is the bare command name', () => {
        // The `aspire` literal would fall through `ResolveAspireCliBundle`'s
        // File.Exists guard and emit a warning rather than fall back, so leaving
        // the env var unset is the correct behavior in that case.
        const collection = createFakeCollection();
        collection.entries.set(ASPIRE_CLI_PATH_ENV_VAR, '/stale/aspire');

        const applied = syncAspireCliPathEnvironment(collection, makeDeps({ getConfiguredPath: () => 'aspire' }));

        assert.strictEqual(applied, undefined);
        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
    });

    test('clears AspireCliPath when the configured absolute path does not exist', () => {
        const collection = createFakeCollection();
        collection.entries.set(ASPIRE_CLI_PATH_ENV_VAR, '/stale/aspire');

        const applied = syncAspireCliPathEnvironment(collection, makeDeps({
            getConfiguredPath: () => '/missing/aspire',
            fileExists: () => false,
        }));

        assert.strictEqual(applied, undefined);
        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
        assert.strictEqual(collection.description, undefined);
    });

    test('writes the contributed-environment description so contributors can see why the variable is set', () => {
        const collection = createFakeCollection();

        syncAspireCliPathEnvironment(collection, makeDeps({ getConfiguredPath: () => '/abs/aspire' }));

        assert.ok(typeof collection.description === 'string' && collection.description.length > 0, 'description should be populated');
    });

    test('clears the contributed-environment description when no variable is set', () => {
        const collection = createFakeCollection();

        syncAspireCliPathEnvironment(collection, makeDeps({ getConfiguredPath: () => 'aspire' }));

        assert.strictEqual(collection.description, undefined);
    });
});

suite('cliPathEnvironment.registerCliPathEnvironmentSync tests', () => {
    let onDidChangeConfigurationStub: sinon.SinonStub;
    let configChangeHandler: ((event: vscode.ConfigurationChangeEvent) => void) | undefined;
    let subscriptions: vscode.Disposable[];

    setup(() => {
        configChangeHandler = undefined;
        subscriptions = [];
        onDidChangeConfigurationStub = sinon.stub(vscode.workspace, 'onDidChangeConfiguration').callsFake((handler) => {
            configChangeHandler = handler as (event: vscode.ConfigurationChangeEvent) => void;
            return { dispose: () => { } };
        });
    });

    teardown(() => {
        onDidChangeConfigurationStub.restore();
        subscriptions.forEach(s => s.dispose());
        resetRejectedConfiguredCliPathForForwarding();
    });

    test('applies current setting on registration and re-applies when aspireCliExecutablePath changes', () => {
        const collection = createFakeCollection();
        let configured = '/abs/aspire';
        const onForwardedPathChanged = sinon.stub();

        registerCliPathEnvironmentSync(collection, subscriptions, makeDeps({
            getConfiguredPath: () => configured,
        }), onForwardedPathChanged);

        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/abs/aspire', 'should sync on registration');
        assert.ok(configChangeHandler, 'should register an onDidChangeConfiguration handler');
        assert.strictEqual(onForwardedPathChanged.callCount, 0, 'initial sync should not recreate existing terminals');

        configured = '';
        const fakeEvent: vscode.ConfigurationChangeEvent = {
            affectsConfiguration: (section) => section === 'aspire.aspireCliExecutablePath',
        };
        configChangeHandler!(fakeEvent);

        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false, 'should clear when setting is removed');
        assert.deepStrictEqual(onForwardedPathChanged.firstCall.args, ['/abs/aspire', undefined]);
    });

    test('does not notify when aspireCliExecutablePath changes but the forwarded value stays unchanged', () => {
        const collection = createFakeCollection();
        let configured = '/missing/aspire';
        const onForwardedPathChanged = sinon.stub();

        registerCliPathEnvironmentSync(collection, subscriptions, makeDeps({
            getConfiguredPath: () => configured,
            fileExists: () => false,
        }), onForwardedPathChanged);

        configured = '/another-missing/aspire';
        const fakeEvent: vscode.ConfigurationChangeEvent = {
            affectsConfiguration: (section) => section === 'aspire.aspireCliExecutablePath',
        };
        configChangeHandler!(fakeEvent);

        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
        assert.strictEqual(onForwardedPathChanged.callCount, 0);
    });

    test('re-applies the contributed path when CLI resolution rejects and later accepts the setting', async () => {
        const collection = createFakeCollection();
        const configuredPath = path.join(
            path.parse(process.cwd()).root,
            'abs',
            process.platform === 'win32' ? 'aspire.exe' : 'aspire');
        const onForwardedPathChanged = sinon.stub();
        let configuredPathWorks = false;

        registerCliPathEnvironmentSync(collection, subscriptions, makeDeps({
            getConfiguredPath: () => configuredPath,
            isRejectedForForwarding: isConfiguredCliPathRejectedForForwarding,
        }), onForwardedPathChanged);

        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), configuredPath);

        const resolutionDeps = {
            getConfiguredPath: () => configuredPath,
            getWorkspaceFolders: () => [],
            getDefaultPaths: () => [],
            isConfiguredPathAutoConfigured: () => false,
            findOnPath: async () => 'aspire',
            findAtDefaultPath: async () => undefined,
            tryExecute: async () => configuredPathWorks,
            getExecutableCandidates: (candidate: string) => [candidate],
            setConfiguredPath: async () => { },
            updateResolvedPathForForwarding: () => { },
        };

        await resolveCliPath(resolutionDeps);

        assert.strictEqual(collection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
        assert.deepStrictEqual(
            onForwardedPathChanged.firstCall.args,
            [configuredPath, undefined]);

        configuredPathWorks = true;
        await resolveCliPath(resolutionDeps);

        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), configuredPath);
        assert.deepStrictEqual(
            onForwardedPathChanged.secondCall.args,
            [undefined, configuredPath]);
    });

    test('ignores configuration changes that do not touch aspireCliExecutablePath', () => {
        const collection = createFakeCollection();
        let configured = '/abs/aspire';
        let getConfiguredCalls = 0;
        registerCliPathEnvironmentSync(collection, subscriptions, makeDeps({
            getConfiguredPath: () => {
                getConfiguredCalls++;
                return configured;
            },
        }));

        // Initial sync consumed one call.
        const initialCalls = getConfiguredCalls;
        configured = '/another/aspire';

        const fakeEvent: vscode.ConfigurationChangeEvent = {
            affectsConfiguration: (section) => section === 'aspire.enableAspireCliDebugLogging',
        };
        configChangeHandler!(fakeEvent);

        assert.strictEqual(getConfiguredCalls, initialCalls, 'should not re-read setting on unrelated changes');
        assert.strictEqual(collection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/abs/aspire');
    });

    test('returned disposable is also captured in subscriptions for activation lifetime cleanup', () => {
        const collection = createFakeCollection();
        const disposable = registerCliPathEnvironmentSync(collection, subscriptions, makeDeps({ getConfiguredPath: () => '/abs/aspire' }));

        assert.strictEqual(subscriptions.length, 1, 'registration should push a disposable onto subscriptions');
        assert.strictEqual(typeof disposable.dispose, 'function');
    });

    test('initialization waits for the first CLI path resolution', async () => {
        let completeResolution: (() => void) | undefined;
        const resolution = new Promise<void>(resolve => completeResolution = resolve);
        let initializationCompleted = false;
        const initialization = initializeCliPathEnvironmentSync(
            createFakeCollection(),
            subscriptions,
            makeDeps(),
            undefined,
            () => resolution);
        void initialization.then(() => initializationCompleted = true);

        await Promise.resolve();
        assert.strictEqual(initializationCompleted, false);

        completeResolution!();
        await initialization;
        assert.strictEqual(initializationCompleted, true);
    });
});

suite('CliPathEnvironmentSynchronizer tests', () => {
    // Workspace folder roots have to be fully qualified for the host platform. A driveless
    // '/repo/a' becomes '\repo\a' on Windows, which is drive-relative rather than absolute, and
    // isAbsoluteCliPath rejects an expanded ${workspaceFolder} candidate built from it -- so the
    // resolver would fall through to PATH instead of probing the configured path these tests are about.
    const folderAPath = path.resolve('/repo/a');
    const folderBPath = path.resolve('/repo/b');
    const folderA = createWorkspaceFolder('a', folderAPath, 0);
    const folderB = createWorkspaceFolder('b', folderBPath, 1);

    // expandConfiguredCliPath normalizes the expanded token with the platform's path library.
    const folderACliPath = path.join(folderAPath, 'aspire');
    const folderBCliPath = path.join(folderBPath, 'aspire');

    test('applies independent AspireCliPath mutations and clears a removed folder', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const paths = new Map([
            [folderA.uri.toString(), '/repo/a/aspire'],
            [folderB.uri.toString(), '/repo/b/aspire'],
        ]);
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                cliPath: target.kind === 'workspaceFolder' ? paths.get(target.workspaceFolder.uri.toString())! : '/window/aspire',
                available: true,
                source: 'configured',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as CliPathResolver;
        const onForwardedPathChanged = sinon.stub();
        const subscriptions: vscode.Disposable[] = [];
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            subscriptions,
            onForwardedPathChanged,
            createSynchronizerDependencies([folderA, folderB], workspaceFoldersEmitter.event));

        await synchronizer.initialize();

        assert.strictEqual(scopedCollections.get(folderA.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/a/aspire');
        assert.strictEqual(scopedCollections.get(folderB.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/b/aspire');
        assert.strictEqual(
            (globalCollection as unknown as ReturnType<typeof createFakeCollection>).entries.has(ASPIRE_CLI_PATH_ENV_VAR),
            false,
            'an unscoped mutation would leak into every open workspace folder');

        workspaceFoldersEmitter.fire({ added: [], removed: [folderA] });

        assert.strictEqual(scopedCollections.get(folderA.uri.toString())?.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
        assert.strictEqual(scopedCollections.get(folderB.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/b/aspire');
        assert.ok(onForwardedPathChanged.calledOnce);
        assert.strictEqual(onForwardedPathChanged.firstCall.args[0].kind, 'workspaceFolder');
        assert.strictEqual(onForwardedPathChanged.firstCall.args[0].workspaceFolder.uri.toString(), folderA.uri.toString());
        assert.deepStrictEqual(onForwardedPathChanged.firstCall.args.slice(1), ['/repo/a/aspire', undefined]);

        synchronizer.dispose();
        subscriptions.forEach(disposable => disposable.dispose());
        forwardingEmitter.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('updates only changed forwarded paths and re-resolves all targets after trust is granted', async () => {
        const configurationEmitter = new vscode.EventEmitter<vscode.ConfigurationChangeEvent>();
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const trustEmitter = new vscode.EventEmitter<void>();
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const paths = new Map([
            [folderA.uri.toString(), '/repo/a/aspire'],
            [folderB.uri.toString(), '/repo/b/aspire'],
        ]);
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                cliPath: target.kind === 'workspaceFolder' ? paths.get(target.workspaceFolder.uri.toString())! : 'aspire',
                available: true,
                source: target.kind === 'workspaceFolder' ? 'configured' : 'path',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as CliPathResolver;
        const onForwardedPathChanged = sinon.stub();
        const synchronizer = new CliPathEnvironmentSynchronizer(
            createFakeGlobalCollection(scopedCollections),
            resolver,
            [],
            onForwardedPathChanged,
            createSynchronizerDependencies(
                [folderA, folderB],
                workspaceFoldersEmitter.event,
                configurationEmitter.event,
                trustEmitter.event));
        await synchronizer.initialize();

        paths.set(folderA.uri.toString(), '/repo/a/next-aspire');
        configurationEmitter.fire({
            affectsConfiguration: (section, scope) => section === 'aspire.aspireCliExecutablePath'
                && (scope === undefined || scope.toString() === folderA.uri.toString()),
        });
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(scopedCollections.get(folderA.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/a/next-aspire');
        assert.strictEqual(scopedCollections.get(folderB.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/b/aspire');
        assert.strictEqual(onForwardedPathChanged.callCount, 1);
        assert.strictEqual(onForwardedPathChanged.firstCall.args[0].workspaceFolder.uri.toString(), folderA.uri.toString());

        paths.set(folderB.uri.toString(), '/repo/b/trusted-aspire');
        trustEmitter.fire();
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(scopedCollections.get(folderB.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/repo/b/trusted-aspire');
        assert.strictEqual(onForwardedPathChanged.callCount, 2);
        assert.strictEqual(onForwardedPathChanged.secondCall.args[0].workspaceFolder.uri.toString(), folderB.uri.toString());

        synchronizer.dispose();
        configurationEmitter.dispose();
        forwardingEmitter.dispose();
        trustEmitter.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('clears a persisted folder mutation when the folder is removed before resolution completes', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const scopedCollection = globalCollection.getScoped({ workspaceFolder: folderA }) as unknown as ReturnType<typeof createFakeCollection>;
        scopedCollection.replace(ASPIRE_CLI_PATH_ENV_VAR, '/persisted/aspire');
        let completeFolderResolution: ((result: { cliPath: string; available: boolean; source: 'configured' }) => void) | undefined;
        const folderResolution = new Promise<{ cliPath: string; available: boolean; source: 'configured' }>(resolve => {
            completeFolderResolution = resolve;
        });
        const resolver = {
            resolve: sinon.stub().callsFake((target: CliPathResolutionTarget) => target.kind === 'workspaceFolder'
                ? folderResolution
                : Promise.resolve({ cliPath: 'aspire', available: true, source: 'path' })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as CliPathResolver;
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            createSynchronizerDependencies([folderA], workspaceFoldersEmitter.event));

        const initialization = synchronizer.initialize();
        workspaceFoldersEmitter.fire({ added: [], removed: [folderA] });

        assert.strictEqual(scopedCollection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);

        completeFolderResolution!({ cliPath: '/repo/a/aspire', available: true, source: 'configured' });
        await initialization;
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(scopedCollection.entries.has(ASPIRE_CLI_PATH_ENV_VAR), false);

        synchronizer.dispose();
        forwardingEmitter.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('clears a persisted global mutation before resolving open workspace folders', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const globalEntries = (globalCollection as unknown as ReturnType<typeof createFakeCollection>).entries;
        const scopedCollection = globalCollection.getScoped({ workspaceFolder: folderA }) as unknown as ReturnType<typeof createFakeCollection>;
        globalCollection.replace(ASPIRE_CLI_PATH_ENV_VAR, '/persisted/window-aspire');
        let completeResolution: ((result: { cliPath: string; available: boolean; source: 'path' }) => void) | undefined;
        const resolution = new Promise<{ cliPath: string; available: boolean; source: 'path' }>(resolve => {
            completeResolution = resolve;
        });
        const resolver = {
            resolve: sinon.stub().returns(resolution),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as CliPathResolver;
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            createSynchronizerDependencies([folderA], workspaceFoldersEmitter.event));

        const initialization = synchronizer.initialize();

        assert.strictEqual(globalEntries.has(ASPIRE_CLI_PATH_ENV_VAR), false);

        completeResolution!({ cliPath: '/resolved/aspire', available: true, source: 'path' });
        await initialization;

        assert.strictEqual(globalEntries.has(ASPIRE_CLI_PATH_ENV_VAR), false);
        assert.strictEqual(scopedCollection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/resolved/aspire');

        synchronizer.dispose();
        forwardingEmitter.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('applies the latest path once when the real resolver emits forwarding during resolution', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const globalCollectionState = globalCollection as unknown as ReturnType<typeof createFakeCollection>;
        const replaceSpy = sinon.spy(globalCollectionState, 'replace');
        const resolverDependencies: CliPathDependencies = {
            getConfiguredPath: () => 'relative-aspire',
            getWorkspaceFolders: () => [],
            getDefaultPaths: () => [],
            isConfiguredPathAutoConfigured: () => false,
            findOnPath: async () => '/resolved/aspire',
            findAtDefaultPath: async () => undefined,
            tryExecute: async () => false,
            getExecutableCandidates: candidate => [candidate],
            setConfiguredPath: async () => { },
        };
        const resolver = new CliPathResolver(resolverDependencies);
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            createSynchronizerDependencies([], workspaceFoldersEmitter.event));

        await synchronizer.initialize();
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(globalCollectionState.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/resolved/aspire');
        assert.ok(replaceSpy.calledOnceWithExactly(ASPIRE_CLI_PATH_ENV_VAR, '/resolved/aspire'));

        synchronizer.dispose();
        resolver.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('applies the latest folder path once when the real resolver emits forwarding during resolution', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const scopedCollection = globalCollection.getScoped({ workspaceFolder: folderA }) as unknown as ReturnType<typeof createFakeCollection>;
        const replaceSpy = sinon.spy(scopedCollection, 'replace');
        const resolverDependencies: CliPathDependencies = {
            getConfiguredPath: target => target.kind === 'workspaceFolder' ? '${workspaceFolder}/aspire' : '',
            getWorkspaceFolders: () => [folderA],
            getDefaultPaths: () => [],
            isConfiguredPathAutoConfigured: () => false,
            findOnPath: async () => '/fallback/aspire',
            findAtDefaultPath: async () => undefined,
            tryExecute: async candidate => candidate === folderACliPath,
            getExecutableCandidates: candidate => [candidate],
            setConfiguredPath: async () => { },
        };
        const resolver = new CliPathResolver(resolverDependencies);
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            createSynchronizerDependencies([folderA], workspaceFoldersEmitter.event));

        await synchronizer.initialize();
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(scopedCollection.entries.get(ASPIRE_CLI_PATH_ENV_VAR), folderACliPath);
        assert.ok(replaceSpy.calledOnceWithExactly(ASPIRE_CLI_PATH_ENV_VAR, folderACliPath));

        synchronizer.dispose();
        resolver.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('does not publish a removed folder CLI from an in-flight window resolution', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        const globalCollectionState = globalCollection as unknown as ReturnType<typeof createFakeCollection>;
        let workspaceFolders: readonly vscode.WorkspaceFolder[] = [folderA];
        let completeOldProbe: ((result: boolean) => void) | undefined;
        let oldProbeStarted: (() => void) | undefined;
        const oldProbeStart = new Promise<void>(resolve => oldProbeStarted = resolve);
        const oldProbe = new Promise<boolean>(resolve => completeOldProbe = resolve);
        const resolverDependencies: CliPathDependencies = {
            getConfiguredPath: target => target.kind === 'window' ? '${workspaceFolder}/aspire' : '',
            getWorkspaceFolders: () => workspaceFolders,
            getDefaultPaths: () => [],
            isConfiguredPathAutoConfigured: () => false,
            findOnPath: async () => '/fallback/aspire',
            findAtDefaultPath: async () => undefined,
            tryExecute: candidate => {
                if (candidate === folderACliPath) {
                    oldProbeStarted!();
                    return oldProbe;
                }
                return Promise.resolve(false);
            },
            getExecutableCandidates: candidate => [candidate],
            setConfiguredPath: async () => { },
        };
        const resolver = new CliPathResolver(resolverDependencies);
        const synchronizerDependencies = {
            ...createSynchronizerDependencies([folderA], workspaceFoldersEmitter.event),
            getWorkspaceFolders: () => workspaceFolders,
        };
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            synchronizerDependencies);

        const initialization = synchronizer.initialize();
        await oldProbeStart;
        workspaceFolders = [];
        workspaceFoldersEmitter.fire({ added: [], removed: [folderA] });
        completeOldProbe!(true);
        await initialization;
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(globalCollectionState.entries.get(ASPIRE_CLI_PATH_ENV_VAR), '/fallback/aspire');

        synchronizer.dispose();
        resolver.dispose();
        workspaceFoldersEmitter.dispose();
    });

    test('re-resolves surviving folders whose named workspace token changes', async () => {
        const workspaceFoldersEmitter = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const scopedCollections = new Map<string, ReturnType<typeof createFakeCollection>>();
        const globalCollection = createFakeGlobalCollection(scopedCollections);
        let workspaceFolders: readonly vscode.WorkspaceFolder[] = [folderA, folderB];
        const resolverDependencies: CliPathDependencies = {
            getConfiguredPath: target => target.kind === 'workspaceFolder' && target.workspaceFolder.uri.toString() === folderA.uri.toString()
                ? '${workspaceFolder:b}/aspire'
                : '',
            getWorkspaceFolders: () => workspaceFolders,
            getDefaultPaths: () => [],
            isConfiguredPathAutoConfigured: () => false,
            findOnPath: async () => 'aspire',
            findAtDefaultPath: async () => undefined,
            tryExecute: async candidate => candidate === folderBCliPath,
            getExecutableCandidates: candidate => [candidate],
            setConfiguredPath: async () => { },
        };
        const resolver = new CliPathResolver(resolverDependencies);
        const synchronizerDependencies = {
            ...createSynchronizerDependencies([folderA, folderB], workspaceFoldersEmitter.event),
            getWorkspaceFolders: () => workspaceFolders,
        };
        const synchronizer = new CliPathEnvironmentSynchronizer(
            globalCollection,
            resolver,
            [],
            undefined,
            synchronizerDependencies);

        await synchronizer.initialize();
        assert.strictEqual(
            scopedCollections.get(folderA.uri.toString())?.entries.get(ASPIRE_CLI_PATH_ENV_VAR),
            folderBCliPath);

        workspaceFolders = [folderA];
        workspaceFoldersEmitter.fire({ added: [], removed: [folderB] });
        await new Promise<void>(resolve => setImmediate(resolve));

        assert.strictEqual(
            scopedCollections.get(folderA.uri.toString())?.entries.has(ASPIRE_CLI_PATH_ENV_VAR),
            false);

        synchronizer.dispose();
        resolver.dispose();
        workspaceFoldersEmitter.dispose();
    });
});

function createFakeGlobalCollection(
    scopedCollections: Map<string, ReturnType<typeof createFakeCollection>>,
): vscode.GlobalEnvironmentVariableCollection {
    const globalCollection = createFakeCollection() as unknown as CliPathEnvironmentCollection & {
        getScoped(scope: vscode.EnvironmentVariableScope): CliPathEnvironmentCollection;
    };
    globalCollection.getScoped = scope => {
        const key = scope.workspaceFolder!.uri.toString();
        let collection = scopedCollections.get(key);
        if (!collection) {
            collection = createFakeCollection();
            scopedCollections.set(key, collection);
        }
        return collection;
    };
    return globalCollection as unknown as vscode.GlobalEnvironmentVariableCollection;
}

function createSynchronizerDependencies(
    workspaceFolders: readonly vscode.WorkspaceFolder[],
    onDidChangeWorkspaceFolders: vscode.Event<vscode.WorkspaceFoldersChangeEvent>,
    onDidChangeConfiguration: vscode.Event<vscode.ConfigurationChangeEvent> = createNoopEvent(),
    onDidGrantWorkspaceTrust: vscode.Event<void> = createNoopEvent(),
): CliPathEnvironmentSynchronizerDependencies {
    return {
        getWorkspaceFolders: () => workspaceFolders,
        getForwardablePath: (cliPath: string | undefined) => cliPath === 'aspire' ? undefined : cliPath,
        onDidChangeConfiguration,
        onDidChangeWorkspaceFolders,
        onDidGrantWorkspaceTrust,
    };
}

function createNoopEvent<T>(): vscode.Event<T> {
    return () => ({ dispose: () => { } });
}
