import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { SafeAppHostTargetResolver } from '../lm/safeAppHostTargetResolver';
import {
    __resetLaunchFailureJournalForTests,
    LaunchFailureJournal,
    launchFailureCategories,
    launchFailureControllers,
    launchFailureExitCodeBuckets,
    launchFailureModes,
    launchFailureProviderKinds,
    launchFailureStages,
    normalizeLaunchFailure,
    readLatestLaunchFailures,
    recordLaunchFailureForAppHostPath,
    type LaunchFailureInput,
} from '../services/launchFailureJournal';
import {
    __resetAppHostIdentityRegistryForTests,
    getOrCreateIdentityForCurrentAppHostTarget,
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
        __resetLaunchFailureJournalForTests();
        workspaceRoot = createFixtureDirectory('workspace');
        appHostProjectPath = path.join(workspaceRoot, 'AppHost', 'AppHost.csproj');
        fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
        fs.writeFileSync(appHostProjectPath, appHostProjectContents);

        resolver = new SafeAppHostTargetResolver(new FakeDiscoveryService());
    });

    teardown(() => {
        __resetLaunchFailureJournalForTests();
        __resetAppHostIdentityRegistryForTests();
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
    });

    suite('LaunchFailureJournal', () => {
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
            const journalIdentity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            const resolverIdentity = resolver.getIdentityForAppHostPath(appHostProjectPath);

            assert.strictEqual(journalIdentity, resolverIdentity);
            assert.strictEqual(journalIdentity.startsWith('apphost-'), true);
            assert.strictEqual(journalIdentity.includes(workspaceRoot), false);
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

            assert.deepStrictEqual(
                readLatestLaunchFailures(projectPath).map(record => record.stage),
                ['build']);
            assert.deepStrictEqual(
                readLatestLaunchFailures(sourcePath).map(record => record.stage),
                ['dcpStartup']);
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

            assert.deepStrictEqual(readLatestLaunchFailures(linkedTarget), []);
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

            assert.deepStrictEqual(
                readLatestLaunchFailures(appHostProjectPath).map(record => record.stage),
                ['build']);
        });

        test('keeps the latest five failures per AppHost in latest-first order', () => {
            let now = 1_000;
            const journal = new LaunchFailureJournal({ now: () => now });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);

            for (let index = 0; index < 6; index++) {
                journal.record(identity, createFailure());
                now++;
            }

            assert.deepStrictEqual(journal.readLatest(identity).map(record => record.sequence), [6, 5, 4, 3, 2]);
        });

        test('keeps the latest fifty failures globally', () => {
            const journal = new LaunchFailureJournal({ now: () => 1_000 });

            for (let index = 0; index < 51; index++) {
                const identity = getOrCreateIdentityForCurrentAppHostTarget(path.join(workspaceRoot, `AppHost${index}.csproj`));
                journal.record(identity, createFailure());
            }

            const records = journal.readLatest();
            assert.strictEqual(records.length, 50);
            assert.deepStrictEqual(records.map(record => record.sequence), Array.from({ length: 50 }, (_, index) => 51 - index));
        });

        test('prunes failures after the thirty minute window on reads', () => {
            let now = 1_000;
            const journal = new LaunchFailureJournal({ now: () => now });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            journal.record(identity, createFailure());

            now += 30 * 60 * 1_000;
            assert.deepStrictEqual(journal.readLatest(identity), []);
            assert.deepStrictEqual(journal.readLatest(), []);
        });

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
            const journal = new LaunchFailureJournal({ now: () => 123_456 });
            const identity = getOrCreateIdentityForCurrentAppHostTarget(appHostProjectPath);
            journal.record(identity, normalized);
            const records = journal.readLatest(identity);

            assert.deepStrictEqual(normalized, {
                stage: 'debugSession',
                category: 'permissionDenied',
                controller: 'editor',
                mode: 'debug',
                providerKind: 'node',
                exitCodeBucket: 'other',
            });
            assert.deepStrictEqual(Object.keys(records[0]).sort(), [
                'appHostIdentity',
                'category',
                'controller',
                'exitCodeBucket',
                'mode',
                'providerKind',
                'recordedAt',
                'sequence',
                'stage',
            ]);

            const serialized = JSON.stringify({ journal, normalized, records });
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

        test('rejects a forged opaque identity instead of retaining a path', () => {
            const journal = new LaunchFailureJournal({ now: () => 123_456 });
            const rawPath = '/private/forged-apphost-path';

            assert.throws(() => journal.record(rawPath as any, createFailure()), /opaque AppHost identity/);
            assert.strictEqual(JSON.stringify(journal).includes(rawPath), false);
        });
    });
});
