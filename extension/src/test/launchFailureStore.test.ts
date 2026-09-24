import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';

import { SafeAppHostTargetResolver } from '../lm/safeAppHostTargetResolver';
import {
    resetLaunchFailureStore,
    LaunchFailureStore,
    getLaunchFailureMode,
    launchFailureCategories,
    launchFailureControllers,
    launchFailureExitCodeBuckets,
    launchFailureModes,
    launchFailureProviderKinds,
    launchFailureStages,
    normalizeLaunchFailure,
    readLatestLaunchFailure,
    recordLaunchFailureForAppHostPath,
    type LaunchFailureInput,
    type SanitizedLaunchFailure,
} from '../services/launchFailureStore';
import {
    __resetAppHostIdentityRegistryForTests,
    getOrCreateIdentityForCurrentAppHostTarget,
    type OpaqueAppHostIdentity,
} from '../utils/appHostIdentity';
import {
    appHostProjectContents,
    createFixtureDirectory,
    FakeDiscoveryService,
} from './helpers/editorAssistanceTestSupport';

suite('Editor assistance AppHost services', () => {
    let workspaceRoot: string;
    let resolver: SafeAppHostTargetResolver;
    let appHostProjectPath: string;

    setup(() => {
        __resetAppHostIdentityRegistryForTests();
        resetLaunchFailureStore();
        workspaceRoot = createFixtureDirectory('workspace');
        appHostProjectPath = path.join(workspaceRoot, 'AppHost', 'AppHost.csproj');
        fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
        fs.writeFileSync(appHostProjectPath, appHostProjectContents);

        resolver = new SafeAppHostTargetResolver(new FakeDiscoveryService());
    });

    teardown(() => {
        resetLaunchFailureStore();
        __resetAppHostIdentityRegistryForTests();
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
    });

    suite('LaunchFailureStore', () => {
        const createFailure = (overrides: Partial<LaunchFailureInput> = {}) => normalizeLaunchFailure({
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'dotnet',
            ...overrides,
        });

        test('rejects runtime mutation of canonical launch failure collections', () => {
            const canonicalCollections = [
                ['stages', launchFailureStages],
                ['categories', launchFailureCategories],
                ['controllers', launchFailureControllers],
                ['modes', launchFailureModes],
                ['provider kinds', launchFailureProviderKinds],
                ['exit code buckets', launchFailureExitCodeBuckets],
            ] as const;

            for (const [name, collection] of canonicalCollections) {
                const original = [...collection];
                const mutableCollection = collection as unknown as string[];

                try {
                    assert.throws(
                        () => mutableCollection.push(`unsafe-${name}`),
                        TypeError,
                        `${name} should reject runtime mutation`);
                    assert.deepStrictEqual(collection, original);
                }
                finally {
                    if (mutableCollection.length > original.length) {
                        mutableCollection.splice(original.length);
                    }
                }
            }
        });

        test('uses the shared opaque AppHost identity registry', () => {
            const storeIdentity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            const resolverIdentity = resolver.getIdentityForAppHostPath(appHostProjectPath);

            assert.strictEqual(storeIdentity, resolverIdentity);
            assert.strictEqual(storeIdentity.startsWith('apphost-'), true);
            assert.strictEqual(storeIdentity.includes(workspaceRoot), false);
        });

        test('keeps opaque identities stable as sibling path shapes appear and disappear', () => {
            const directoryPath = path.join(workspaceRoot, 'ChangingIdentity');
            const projectPath = path.join(directoryPath, 'ChangingIdentity.csproj');
            const sourcePath = path.join(directoryPath, 'Program.cs');
            fs.mkdirSync(directoryPath, { recursive: true });
            fs.writeFileSync(projectPath, '<Project />');

            const identity = getOrCreateIdentityForCurrentAppHostTarget(projectPath);

            fs.writeFileSync(sourcePath, 'var builder = DistributedApplication.CreateBuilder(args);');
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(sourcePath), identity);

            fs.unlinkSync(projectPath);
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(sourcePath), identity);

            fs.writeFileSync(projectPath, '<Project />');
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(projectPath), identity);

            fs.unlinkSync(sourcePath);
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(projectPath), identity);
        });

        test('preserves issued path histories when project-source uniqueness changes', () => {
            const directoryPath = path.join(workspaceRoot, 'Rebinding');
            const projectPath = path.join(directoryPath, 'AppHost.csproj');
            const secondProjectPath = path.join(directoryPath, 'Other.csproj');
            const sourcePath = path.join(directoryPath, 'Program.cs');
            fs.mkdirSync(directoryPath, { recursive: true });
            fs.writeFileSync(projectPath, '<Project />');
            fs.writeFileSync(secondProjectPath, '<Project />');
            fs.writeFileSync(sourcePath, 'var builder = DistributedApplication.CreateBuilder(args);');

            const projectIdentity = getOrCreateIdentityForCurrentAppHostTarget(projectPath);
            const sourceIdentity = getOrCreateIdentityForCurrentAppHostTarget(sourcePath);
            assert.notStrictEqual(projectIdentity, sourceIdentity);

            recordLaunchFailureForAppHostPath(projectPath, {
                stage: 'build',
                category: 'buildFailed',
                controller: 'editor',
            });
            recordLaunchFailureForAppHostPath(sourcePath, {
                stage: 'dcpStartup',
                category: 'processExited',
                controller: 'editor',
            });

            fs.unlinkSync(secondProjectPath);

            assert.strictEqual(readLatestLaunchFailure(projectPath)?.stage, 'build');
            assert.strictEqual(readLatestLaunchFailure(sourcePath)?.stage, 'dcpStartup');
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(projectPath), projectIdentity);
            assert.strictEqual(getOrCreateIdentityForCurrentAppHostTarget(sourcePath), sourceIdentity);
        });

        test('does not return a failure after a symlink retargets', function () {
            const firstTarget = path.join(workspaceRoot, 'FirstTarget', 'AppHost.csproj');
            const secondTarget = path.join(workspaceRoot, 'SecondTarget', 'AppHost.csproj');
            const linkedTarget = path.join(workspaceRoot, 'LinkedTarget', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(firstTarget), { recursive: true });
            fs.mkdirSync(path.dirname(secondTarget), { recursive: true });
            fs.mkdirSync(path.dirname(linkedTarget), { recursive: true });
            fs.writeFileSync(firstTarget, '<Project />');
            fs.writeFileSync(secondTarget, '<Project />');
            try {
                fs.symlinkSync(firstTarget, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            recordLaunchFailureForAppHostPath(linkedTarget, {
                stage: 'build',
                category: 'buildFailed',
                controller: 'editor',
            });

            fs.rmSync(linkedTarget);
            fs.symlinkSync(secondTarget, linkedTarget);

            assert.strictEqual(readLatestLaunchFailure(linkedTarget), undefined);
        });

        test('preserves a failure when the same AppHost file is atomically replaced', () => {
            recordLaunchFailureForAppHostPath(appHostProjectPath, {
                stage: 'build',
                category: 'buildFailed',
                controller: 'editor',
            });

            const replacementPath = `${appHostProjectPath}.replacement`;
            fs.writeFileSync(replacementPath, '<Project />');
            fs.renameSync(replacementPath, appHostProjectPath);

            assert.strictEqual(readLatestLaunchFailure(appHostProjectPath)?.stage, 'build');
        });

        test('keeps only the latest failure per AppHost', () => {
            const store = new LaunchFailureStore();
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            const latest = createFailure({ stage: 'build', category: 'buildFailed' });
            for (let index = 0; index < 6; index++) {
                store.record(identity, createFailure());
            }
            store.record(identity, latest);

            assert.deepStrictEqual(store.read(identity), latest);
        });

        for (const replaceOldest of [false, true]) {
            test(`keeps at most fifty AppHosts in write order, replacing oldest: ${replaceOldest}`, () => {
                const sizes: number[] = [];
                const store = new LaunchFailureStore(
                    { now: () => 1_000 },
                    (_failure, size) => sizes.push(size));
                const identities = Array.from({ length: 51 }, (_, index) =>
                    getOrCreateIdentityForCurrentAppHostTarget(path.join(workspaceRoot, `AppHost${index}.csproj`)));
                const failure = createFailure();
                for (const identity of identities.slice(0, 50)) {
                    store.record(identity, failure);
                }
                assert.deepStrictEqual(store.read(identities[0]), failure);
                if (replaceOldest) {
                    for (let index = 0; index < 6; index++) {
                        store.record(identities[0], failure);
                    }
                }
                store.record(identities[50], failure);

                const evictedIndex = replaceOldest ? 1 : 0;
                assert.deepStrictEqual(
                    identities.map(identity => store.read(identity)),
                    identities.map((_, index) => index === evictedIndex ? undefined : failure));
                assert.deepStrictEqual(sizes, [
                    ...Array.from({ length: 50 }, (_, index) => index + 1),
                    ...Array(replaceOldest ? 7 : 1).fill(50),
                ]);
            });
        }

        test('replacement refreshes expiry but reading does not', () => {
            let now = 1_000;
            const store = new LaunchFailureStore({ now: () => now });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            store.record(identity, createFailure());
            const latest = createFailure({ category: 'buildFailed' });
            now += 60_000;
            store.record(identity, latest);

            now += 30 * 60_000 - 1;
            assert.deepStrictEqual(store.read(identity), latest);
            now++;
            assert.strictEqual(store.read(identity), undefined);
        });

        test('input, callback, and read mutations cannot change retained failure state', () => {
            const failure = { ...createFailure() };
            const expected = { ...failure };
            const store = new LaunchFailureStore(undefined, accepted => {
                Object.assign(accepted, { category: 'timeout', secret: 'callback-secret' });
            });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            store.record(identity, failure);
            failure.category = 'buildFailed';
            const firstRead = store.read(identity);
            assert.deepStrictEqual(firstRead, expected);
            assert.ok(firstRead);
            Object.assign(firstRead, { category: 'permissionDenied', secret: 'read-secret' });

            assert.deepStrictEqual(store.read(identity), expected);
            store.clear();
            assert.strictEqual(store.read(identity), undefined);
        });

        test('revalidates all fields and drops extra input at the storage boundary', () => {
            const store = new LaunchFailureStore();
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            const forged = {
                stage: 'secret-stage',
                category: 'secret-category',
                controller: 'secret-controller',
                mode: 'secret-mode',
                providerKind: 'secret-provider',
                exitCodeBucket: 'secret-exit',
                rawError: 'secret-error',
            };
            store.record(identity, forged as unknown as SanitizedLaunchFailure);
            assert.deepStrictEqual(store.read(identity), {
                stage: 'debugSession',
                category: 'unknown',
                controller: 'editor',
                mode: 'other',
                providerKind: 'other',
                exitCodeBucket: 'none',
            });
        });

        test('uses one failure mode policy for all launch producers', () => {
            for (const noDebug of [false, true]) {
                assert.strictEqual(getLaunchFailureMode('run', noDebug), noDebug ? 'run' : 'debug');
                assert.strictEqual(getLaunchFailureMode('deploy', noDebug), 'deploy');
                assert.strictEqual(getLaunchFailureMode('publish', noDebug), 'publish');
                assert.strictEqual(getLaunchFailureMode('do', noDebug), 'other');
                assert.strictEqual(getLaunchFailureMode('unknown', noDebug), 'other');
                assert.strictEqual(getLaunchFailureMode(undefined, noDebug), 'other');
            }
        });

        test('prunes failures after the thirty minute window on reads', () => {
            let now = 1_000;
            const store = new LaunchFailureStore({ now: () => now });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            store.record(identity, createFailure());

            now += 30 * 60 * 1_000;
            assert.strictEqual(store.read(identity), undefined);
        });

        for (const wallClockDelta of [-3_600_000, 3_600_000]) {
            for (const pruneOn of ['read', 'record']) {
                test(`uses elapsed time for expiry on ${pruneOn} after a ${wallClockDelta}ms wall-clock jump`, () => {
                    const sandbox = sinon.createSandbox();
                    try {
                        const wallClock = sandbox.stub(Date, 'now').returns(10_000_000);
                        const monotonicClock = sandbox.stub(performance, 'now').returns(1_000);
                        const store = new LaunchFailureStore();
                        const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
                        const failure = createFailure();
                        store.record(identity, failure);
                        const globalInput: LaunchFailureInput = {
                            stage: 'build',
                            category: 'buildFailed',
                            controller: 'editor',
                        };
                        recordLaunchFailureForAppHostPath(appHostProjectPath, globalInput);
                        const otherPath = path.join(workspaceRoot, 'Other.csproj');
                        const otherIdentity = getOrCreateIdentityForCurrentAppHostTarget(otherPath);

                        wallClock.returns(10_000_000 + wallClockDelta);
                        monotonicClock.returns(1_000 + 30 * 60_000 - 1);
                        if (pruneOn === 'record') {
                            store.record(otherIdentity, failure);
                            recordLaunchFailureForAppHostPath(otherPath, globalInput);
                        }

                        assert.deepStrictEqual(store.read(identity), failure);
                        assert.deepStrictEqual(readLatestLaunchFailure(appHostProjectPath), normalizeLaunchFailure(globalInput));

                        monotonicClock.returns(1_000 + 30 * 60_000);
                        assert.strictEqual(store.read(identity), undefined);
                        assert.strictEqual(readLatestLaunchFailure(appHostProjectPath), undefined);
                        assert.deepStrictEqual(store.read(otherIdentity), pruneOn === 'record' ? failure : undefined);
                        assert.deepStrictEqual(readLatestLaunchFailure(otherPath), pruneOn === 'record' ? normalizeLaunchFailure(globalInput) : undefined);
                    }
                    finally {
                        sandbox.restore();
                    }
                });
            }
        }

        for (const pruneOn of ['read', 'record']) {
            for (const includeLaterValidRecord of [false, true]) {
                test(`prunes rollback expirations on ${pruneOn}, later valid record: ${includeLaterValidRecord}`, () => {
                    const minute = 60_000;
                    let now = 40 * minute;
                    const acceptedSizes: number[] = [];
                    const store = new LaunchFailureStore(
                        { now: () => now },
                        (_failure, size) => acceptedSizes.push(size));
                    const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
                    const otherIdentity = getOrCreateIdentityForCurrentAppHostTarget(path.join(workspaceRoot, 'Other.csproj'));
                    const laterIdentity = getOrCreateIdentityForCurrentAppHostTarget(path.join(workspaceRoot, 'Later.csproj'));
                    const failure = createFailure();
                    store.record(identity, failure);

                    now = 0;
                    store.record(otherIdentity, failure);
                    if (includeLaterValidRecord) {
                        now = 2 * minute;
                        store.record(laterIdentity, failure);
                    }

                    now = 31 * minute;
                    if (pruneOn === 'record') {
                        store.record(identity, failure);
                        assert.strictEqual(acceptedSizes.at(-1), includeLaterValidRecord ? 2 : 1);
                    }

                    assert.deepStrictEqual(store.read(identity), failure);
                    assert.strictEqual(store.read(otherIdentity), undefined);
                    assert.deepStrictEqual(store.read(laterIdentity), includeLaterValidRecord ? failure : undefined);
                });
            }
        }

        test('bounds provider kinds and exit code buckets', () => {
            const providers = [
                ['coreclr', 'dotnet'],
                ['pwa-node', 'node'],
                ['debugpy', 'python'],
                ['java', 'java'],
                ['go', 'go'],
                ['lldb', 'rust'],
                ['maui', 'maui'],
                ['azure-functions', 'azureFunctions'],
                ['pwa-msedge', 'browser'],
                ['bun', 'bun'],
                ['private-debugger', 'other'],
            ] as const;

            for (const [providerKind, expected] of providers) {
                assert.strictEqual(createFailure({ providerKind }).providerKind, expected);
            }

            assert.strictEqual(createFailure({ exitCode: undefined }).exitCodeBucket, 'none');
            assert.strictEqual(createFailure({ exitCode: 0 }).exitCodeBucket, 'zero');
            assert.strictEqual(createFailure({ exitCode: 1 }).exitCodeBucket, 'one');
            assert.strictEqual(createFailure({ exitCode: 17 }).exitCodeBucket, 'other');
            assert.strictEqual(createFailure({ exitCode: null, signal: 'SIGTERM' }).exitCodeBucket, 'signal');
        });

        test('does not retain raw failure data in normalized, stored, or returned records', () => {
            const secrets = {
                message: 'raw-message-secret',
                stack: 'raw-stack-secret',
                output: 'raw-output-secret',
                path: '/private/raw-path-secret',
                url: 'https://raw-url-secret.example',
                arguments: ['raw-argument-secret'],
                environment: { PRIVATE_ENV: 'raw-environment-secret' },
                token: 'raw-token-secret',
                resourceProperties: { connectionString: 'raw-resource-secret' },
                debugConfiguration: { program: 'raw-debug-config-secret' },
                pid: 424242,
                sessionId: 'raw-session-id-secret',
            };
            const error = Object.assign(new Error(secrets.message), {
                name: 'RawError',
                code: 'EACCES',
                stack: secrets.stack,
                output: secrets.output,
                path: secrets.path,
                url: secrets.url,
                arguments: secrets.arguments,
                environment: secrets.environment,
                token: secrets.token,
                resourceProperties: secrets.resourceProperties,
                debugConfiguration: secrets.debugConfiguration,
                pid: secrets.pid,
                sessionId: secrets.sessionId,
            });
            const rawFailure = {
                stage: 'debugSession',
                controller: 'editor',
                mode: 'debug',
                providerKind: 'node',
                exitCode: 17,
                error,
                ...secrets,
            } as unknown as LaunchFailureInput;
            const normalized = normalizeLaunchFailure(rawFailure);
            const store = new LaunchFailureStore({ now: () => 123_456 });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            store.record(identity, normalized);
            const record = store.read(identity);

            assert.deepStrictEqual(normalized, {
                stage: 'debugSession',
                category: 'permissionDenied',
                controller: 'editor',
                mode: 'debug',
                providerKind: 'node',
                exitCodeBucket: 'other',
            });
            assert.ok(record);
            assert.deepStrictEqual(Object.keys(record).sort(), [
                'category',
                'controller',
                'exitCodeBucket',
                'mode',
                'providerKind',
                'stage',
            ]);

            assert.deepStrictEqual(record, normalized);
            const serialized = JSON.stringify({ normalized, record });
            for (const secret of [
                secrets.message,
                secrets.stack,
                secrets.output,
                secrets.path,
                secrets.url,
                secrets.arguments[0],
                secrets.environment.PRIVATE_ENV,
                secrets.token,
                secrets.resourceProperties.connectionString,
                secrets.debugConfiguration.program,
                String(secrets.pid),
                secrets.sessionId,
            ]) {
                assert.strictEqual(serialized.includes(secret), false, `Retained raw value: ${secret}`);
            }
        });

        test('rejects forged strings and coercible objects instead of retaining raw identity data', () => {
            const store = new LaunchFailureStore({ now: () => 123_456 });
            const rawPath = '/private/forged-apphost-path';

            for (const identity of [rawPath, { rawPath, toString: () => 'apphost-99' }]) {
                assert.throws(
                    () => store.record(identity as OpaqueAppHostIdentity, createFailure()),
                    /opaque AppHost identity/);
            }
            assert.strictEqual(store.read('apphost-99' as OpaqueAppHostIdentity), undefined);
        });
    });
});
