import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    ASPIRE_CLI_PATH_ENV_VAR,
    CliPathEnvironmentCollection,
    CliPathEnvironmentDependencies,
    createAspireCliPathProcessEnvironment,
    getForwardableAspireCliPath,
    registerCliPathEnvironmentSync,
    syncAspireCliPathEnvironment,
} from '../utils/cliPathEnvironment';

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

function makeDeps(overrides: Partial<CliPathEnvironmentDependencies> = {}): CliPathEnvironmentDependencies {
    return {
        getConfiguredPath: () => '',
        isAbsolute: (cliPath: string) => cliPath.startsWith('/') || /^[A-Za-z]:[\\/]/.test(cliPath),
        fileExists: (cliPath: string) => cliPath.endsWith('/aspire') || cliPath.endsWith('\\aspire.exe') || cliPath.endsWith('/aspire.exe'),
        realpath: (cliPath: string) => cliPath,
        log: () => { },
        ...overrides,
    };
}

function normalizeCandidate(candidate: string): string {
    return candidate.replace(/\\/g, '/');
}

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
});
