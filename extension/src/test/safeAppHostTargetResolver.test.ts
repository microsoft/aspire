import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';

import { SafeAppHostTargetResolver } from '../lm/safeAppHostTargetResolver';
import { __resetLaunchFailureJournalForTests } from '../services/launchFailureJournal';
import { __resetAppHostIdentityRegistryForTests } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    addCandidate,
    appHostProjectContents,
    assertResolved,
    createFixtureDirectory,
    createWorkspaceFolder,
    FakeDiscoveryService,
} from './helpers/editorAssistanceTestSupport';
import { ScriptedRealpath } from './helpers/scriptedRealpath';

function createUnsafeModelTriggeredError(workspaceRoot: string): {
    readonly error: Error;
    readonly sentinels: readonly string[];
} {
    const sentinels = [
        path.join(workspaceRoot, 'private', 'AppHost.csproj'),
        'dashboard-token-sentinel',
        'RAW_CLI_STDOUT_SENTINEL',
        'CREDENTIAL_SENTINEL=editor-secret',
        'STACK_MESSAGE_SENTINEL',
    ] as const;
    const error = new Error([
        sentinels[0],
        `https://dashboard.example.invalid/login?t=${sentinels[1]}`,
        sentinels[2],
        sentinels[3],
    ].join(' | '));
    error.stack = `${error.name}: ${error.message}\n    at ${sentinels[4]}`;
    return { error, sentinels };
}

suite('Editor assistance AppHost services', () => {
    let workspaceRoot: string;
    let outsideRoot: string;
    let workspaceFoldersStub: sinon.SinonStub;
    let discoveryService: FakeDiscoveryService;
    let resolver: SafeAppHostTargetResolver;
    let appHostProjectPath: string;

    setup(() => {
        __resetAppHostIdentityRegistryForTests();
        __resetLaunchFailureJournalForTests();
        workspaceRoot = createFixtureDirectory('workspace');
        outsideRoot = createFixtureDirectory('outside');
        appHostProjectPath = path.join(workspaceRoot, 'AppHost', 'AppHost.csproj');
        fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
        fs.writeFileSync(appHostProjectPath, appHostProjectContents);

        workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([
            createWorkspaceFolder(workspaceRoot, 'workspace', 0),
        ]);

        discoveryService = new FakeDiscoveryService();
        addCandidate(discoveryService, workspaceRoot, appHostProjectPath);
        resolver = new SafeAppHostTargetResolver(discoveryService);
    });

    teardown(() => {
        __resetLaunchFailureJournalForTests();
        __resetAppHostIdentityRegistryForTests();
        workspaceFoldersStub.restore();
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
        fs.rmSync(outsideRoot, { recursive: true, force: true });
    });

    suite('SafeAppHostTargetResolver', () => {
        test('rejects non-string, blank, and overly long selectors without consulting discovery', async () => {
            const token = new vscode.CancellationTokenSource().token;
            const inputs = [undefined, '   ', 'a'.repeat(4097)] as const;

            for (const input of inputs) {
                const resolution = await resolver.resolveTarget(input, token);
                assert.deepStrictEqual(resolution, { resolved: false, outcome: 'invalidInput' });
            }

            assert.strictEqual(discoveryService.discoverCalls, 0);
        });

        test('rejects absolute selectors as invalidInput', async () => {
            const resolution = await resolver.resolveTarget(appHostProjectPath, new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(resolution, { resolved: false, outcome: 'invalidInput' });
            assert.strictEqual(discoveryService.discoverCalls, 0);
        });

        test('requires workspace-folder qualification in a multi-root workspace even when only one root currently matches', async () => {
            const secondRoot = createFixtureDirectory('second-workspace');
            try {
                const secondAppHost = path.join(secondRoot, 'Other', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(secondAppHost), { recursive: true });
                fs.writeFileSync(secondAppHost, appHostProjectContents);
                addCandidate(discoveryService, secondRoot, secondAppHost);
                workspaceFoldersStub.value([
                    createWorkspaceFolder(workspaceRoot, 'workspace', 0),
                    createWorkspaceFolder(secondRoot, 'second', 1),
                ]);

                const resolution = await resolver.resolveTarget('Other/AppHost.csproj', new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(resolution, {
                    resolved: false,
                    outcome: 'ambiguousAppHost',
                    knownAppHosts: ['second/Other/AppHost.csproj'],
                });
            }
            finally {
                fs.rmSync(secondRoot, { recursive: true, force: true });
            }
        });

        test('resolves a workspace-folder-qualified selector with safe display paths', async () => {
            const secondRoot = createFixtureDirectory('second-workspace');
            try {
                const secondAppHost = path.join(secondRoot, 'Nested', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(secondAppHost), { recursive: true });
                fs.writeFileSync(secondAppHost, appHostProjectContents);
                addCandidate(discoveryService, secondRoot, secondAppHost);
                workspaceFoldersStub.value([
                    createWorkspaceFolder(workspaceRoot, 'workspace', 0),
                    createWorkspaceFolder(secondRoot, 'second', 1),
                ]);

                const resolution = await resolver.resolveTarget('second/Nested/AppHost.csproj', new vscode.CancellationTokenSource().token);

                assertResolved(resolution);
                assert.strictEqual(resolution.target.absolutePath, secondAppHost);
                assert.strictEqual(resolution.target.relativePath, 'Nested/AppHost.csproj');
                assert.strictEqual(resolution.target.displayPath, 'second/Nested/AppHost.csproj');
            }
            finally {
                fs.rmSync(secondRoot, { recursive: true, force: true });
            }
        });

        test('resolves duplicate workspace folder names with deterministic qualifiers', async () => {
            const secondRoot = createFixtureDirectory('second-workspace');
            try {
                const secondAppHost = path.join(secondRoot, 'AppHost', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(secondAppHost), { recursive: true });
                fs.writeFileSync(secondAppHost, appHostProjectContents);
                addCandidate(discoveryService, secondRoot, secondAppHost);
                workspaceFoldersStub.value([
                    createWorkspaceFolder(workspaceRoot, 'workspace', 0),
                    createWorkspaceFolder(secondRoot, 'workspace', 1),
                ]);

                const firstResolution = await resolver.resolveTarget('workspace (1)/AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);
                const secondResolution = await resolver.resolveTarget('workspace (2)/AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);

                assertResolved(firstResolution);
                assertResolved(secondResolution);
                assert.strictEqual(firstResolution.target.absolutePath, appHostProjectPath);
                assert.strictEqual(firstResolution.target.displayPath, 'workspace (1)/AppHost/AppHost.csproj');
                assert.strictEqual(secondResolution.target.absolutePath, secondAppHost);
                assert.strictEqual(secondResolution.target.displayPath, 'workspace (2)/AppHost/AppHost.csproj');
            }
            finally {
                fs.rmSync(secondRoot, { recursive: true, force: true });
            }
        });

        test('uses selector comparison keys to disambiguate case-insensitive workspace folder names', async () => {
            const secondRoot = createFixtureDirectory('second-workspace');
            try {
                const secondAppHost = path.join(secondRoot, 'AppHost', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(secondAppHost), { recursive: true });
                fs.writeFileSync(secondAppHost, appHostProjectContents);
                addCandidate(discoveryService, secondRoot, secondAppHost);
                workspaceFoldersStub.value([
                    createWorkspaceFolder(workspaceRoot, 'Foo', 0),
                    createWorkspaceFolder(secondRoot, 'foo', 1),
                ]);
                const windowsSelectorKey = (value: string) =>
                    value.replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
                const caseInsensitiveResolver = new SafeAppHostTargetResolver(discoveryService, windowsSelectorKey);

                const knownTargets = await caseInsensitiveResolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);
                const firstResolution = await caseInsensitiveResolver.resolveTarget('foo (1)/AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);
                const secondResolution = await caseInsensitiveResolver.resolveTarget('FOO (2)/AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(knownTargets.map(target => target.displayPath), [
                    'Foo (1)/AppHost/AppHost.csproj',
                    'foo (2)/AppHost/AppHost.csproj',
                ]);
                assertResolved(firstResolution);
                assertResolved(secondResolution);
                assert.strictEqual(firstResolution.target.absolutePath, appHostProjectPath);
                assert.strictEqual(secondResolution.target.absolutePath, secondAppHost);
            }
            finally {
                fs.rmSync(secondRoot, { recursive: true, force: true });
            }
        });

        test('reports canceled when discovery is canceled', async () => {
            discoveryService.discoverError = new vscode.CancellationError();

            const resolution = await resolver.resolveTarget('AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(resolution, { resolved: false, outcome: 'canceled' });
        });

        test('reports error when discovery fails', async () => {
            discoveryService.discoverError = new Error('aspire ls failed');

            const resolution = await resolver.resolveTarget('AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(resolution, { resolved: false, outcome: 'error' });
        });

        test('keeps resolver discovery diagnostics free of raw error text', async () => {
            const sandbox = sinon.createSandbox();
            try {
                const warningLog = sandbox.stub(extensionLogOutputChannel, 'warn');
                const { error, sentinels } = createUnsafeModelTriggeredError(workspaceRoot);
                discoveryService.discoverError = error;

                const resolution = await resolver.resolveTarget(
                    'AppHost/AppHost.csproj',
                    new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(resolution, { resolved: false, outcome: 'error' });
                sinon.assert.calledOnceWithExactly(
                    warningLog,
                    'Aspire editor assistance could not enumerate AppHosts.');
                const serialized = JSON.stringify({
                    resolution,
                    logs: warningLog.getCalls().map(call => call.args),
                });
                for (const sentinel of sentinels) {
                    assert.strictEqual(serialized.includes(sentinel), false, `Leaked sentinel: ${sentinel}`);
                }
            }
            finally {
                sandbox.restore();
            }
        });

        test('omits candidates that are outside every workspace folder', async () => {
            const outsideAppHost = path.join(outsideRoot, 'External', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(outsideAppHost), { recursive: true });
            fs.writeFileSync(outsideAppHost, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, outsideAppHost);

            const knownTargets = await resolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(knownTargets.map(target => target.displayPath), ['AppHost/AppHost.csproj']);
        });

        test('enumerates and resolves workspace child paths whose names begin with two dots', async () => {
            const dottedAppHost = path.join(workspaceRoot, '..app', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(dottedAppHost), { recursive: true });
            fs.writeFileSync(dottedAppHost, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, dottedAppHost);

            const token = new vscode.CancellationTokenSource().token;
            const knownTargets = await resolver.enumerateKnownAppHosts(token);
            const resolution = await resolver.resolveTarget('..app/AppHost.csproj', token);

            assert.deepStrictEqual(
                knownTargets.map(target => target.displayPath),
                ['AppHost/AppHost.csproj', '..app/AppHost.csproj']);
            assertResolved(resolution);
            assert.strictEqual(resolution.target.absolutePath, dottedAppHost);
            assert.strictEqual(resolution.target.relativePath, '..app/AppHost.csproj');
            assert.strictEqual(resolution.target.displayPath, '..app/AppHost.csproj');
        });

        test('drops candidates whose real target escapes the workspace', async function () {
            const outsideAppHost = path.join(outsideRoot, 'External.csproj');
            fs.writeFileSync(outsideAppHost, appHostProjectContents);
            const linkedAppHost = path.join(workspaceRoot, 'AppHost', 'Linked.csproj');
            try {
                fs.symlinkSync(outsideAppHost, linkedAppHost);
            }
            catch {
                this.skip();
                return;
            }

            addCandidate(discoveryService, workspaceRoot, linkedAppHost);
            const knownTargets = await resolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(knownTargets.map(target => target.displayPath), ['AppHost/AppHost.csproj']);
        });

        test('drops a candidate whose captured physical target escapes the workspace after the selector looked contained', async () => {
            // Containment and binding are two reads of the same mutable name. A selector that
            // resolves inside the workspace for the containment sample and outside it for the
            // binding sample would otherwise produce a target that displays a workspace-relative
            // path while every operation it carries runs against a file outside the workspace.
            // The script places the retarget exactly in that window, which a real symlink cannot
            // do because both reads happen without an await between them.
            const outsideAppHost = path.join(outsideRoot, 'External.csproj');
            fs.writeFileSync(outsideAppHost, appHostProjectContents);
            const canonicalOutsideAppHost = fs.realpathSync.native(outsideAppHost);
            const movingAppHost = path.join(workspaceRoot, 'AppHost', 'Moving.csproj');
            fs.writeFileSync(movingAppHost, appHostProjectContents);
            const canonicalMovingAppHost = fs.realpathSync.native(movingAppHost);
            addCandidate(discoveryService, workspaceRoot, movingAppHost);

            const scriptedRealpath = new ScriptedRealpath();
            try {
                scriptedRealpath.script(movingAppHost, {
                    results: [canonicalMovingAppHost],
                    thereafter: canonicalOutsideAppHost,
                });

                const knownTargets = await resolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(knownTargets.map(target => target.displayPath), ['AppHost/AppHost.csproj']);
                assert.deepStrictEqual(
                    knownTargets.map(target => target.canonicalPath),
                    [fs.realpathSync.native(appHostProjectPath)]);
            }
            finally {
                scriptedRealpath.restore();
            }
        });

        test('keeps a candidate whose captured physical target stays inside the workspace', async () => {
            // The mirror of the rejection above: containment is decided from the captured path,
            // so a target that never leaves the workspace still enumerates normally.
            const stableAppHost = path.join(workspaceRoot, 'AppHost', 'Stable.csproj');
            fs.writeFileSync(stableAppHost, appHostProjectContents);
            const canonicalStableAppHost = fs.realpathSync.native(stableAppHost);
            addCandidate(discoveryService, workspaceRoot, stableAppHost);

            const scriptedRealpath = new ScriptedRealpath();
            try {
                scriptedRealpath.script(stableAppHost, { results: [canonicalStableAppHost] });

                const knownTargets = await resolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(
                    knownTargets.map(target => target.displayPath),
                    ['AppHost/AppHost.csproj', 'AppHost/Stable.csproj']);
                assert.strictEqual(
                    knownTargets[1].canonicalPath,
                    canonicalStableAppHost);
            }
            finally {
                scriptedRealpath.restore();
            }
        });

        test('enumerates nothing while the workspace is not trusted', async () => {
            const trustStub = sinon.stub(vscode.workspace, 'isTrusted').value(false);
            try {
                const knownTargets = await resolver.enumerateKnownAppHosts(new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(knownTargets, []);
                assert.strictEqual(discoveryService.discoverCalls, 0);
            }
            finally {
                trustStub.restore();
            }
        });

        test('drops registry entries whose identity cannot be rendered faithfully', async () => {
            const invisibleAppHost = path.join(workspaceRoot, 'AppHost', 'App\u200bHost.csproj');
            fs.writeFileSync(invisibleAppHost, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, invisibleAppHost);

            const resolution = await resolver.resolveTarget('AppHost/App\u200bHost.csproj', new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(resolution, {
                resolved: false,
                outcome: 'appHostNotFound',
                knownAppHosts: ['AppHost/AppHost.csproj'],
            });
        });

        test('keeps lexical symlink aliases independently selectable', async function () {
            const linkedTarget = path.join(workspaceRoot, 'Linked', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(linkedTarget), { recursive: true });
            try {
                fs.symlinkSync(appHostProjectPath, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            addCandidate(discoveryService, workspaceRoot, linkedTarget);

            const realResolution = await resolver.resolveTarget('AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);
            const linkedResolution = await resolver.resolveTarget('Linked/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assertResolved(realResolution);
            assertResolved(linkedResolution);
            assert.strictEqual(realResolution.target.absolutePath, appHostProjectPath);
            assert.strictEqual(linkedResolution.target.absolutePath, linkedTarget);
        });

        test('changes target identity when a symlink retargets', async function () {
            const firstRealTarget = path.join(workspaceRoot, 'First', 'AppHost.csproj');
            const secondRealTarget = path.join(workspaceRoot, 'Second', 'AppHost.csproj');
            const linkedTarget = path.join(workspaceRoot, 'Linked', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(firstRealTarget), { recursive: true });
            fs.mkdirSync(path.dirname(secondRealTarget), { recursive: true });
            fs.mkdirSync(path.dirname(linkedTarget), { recursive: true });
            fs.writeFileSync(firstRealTarget, appHostProjectContents);
            fs.writeFileSync(secondRealTarget, appHostProjectContents);
            try {
                fs.symlinkSync(firstRealTarget, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            addCandidate(discoveryService, workspaceRoot, linkedTarget);
            const firstResolution = await resolver.resolveTarget('Linked/AppHost.csproj', new vscode.CancellationTokenSource().token);
            const secondResolution = await resolver.resolveTarget('Linked/AppHost.csproj', new vscode.CancellationTokenSource().token);
            assertResolved(firstResolution);
            assertResolved(secondResolution);
            assert.strictEqual(firstResolution.target.identity, secondResolution.target.identity);

            fs.rmSync(linkedTarget, { force: true });
            fs.symlinkSync(secondRealTarget, linkedTarget);
            const thirdResolution = await resolver.resolveTarget('Linked/AppHost.csproj', new vscode.CancellationTokenSource().token);
            assertResolved(thirdResolution);
            assert.notStrictEqual(firstResolution.target.identity, thirdResolution.target.identity);
        });

        test('preserves target identity when the same AppHost file is atomically replaced', async () => {
            const firstResolution = await resolver.resolveTarget('AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);
            assertResolved(firstResolution);

            const replacementPath = `${appHostProjectPath}.replacement`;
            fs.writeFileSync(replacementPath, `${appHostProjectContents}\n`);
            fs.renameSync(replacementPath, appHostProjectPath);

            const secondResolution = await resolver.resolveTarget('AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token);
            assertResolved(secondResolution);
            assert.strictEqual(firstResolution.target.identity, secondResolution.target.identity);
        });

        test('binds a resolved target to the canonical AppHost its selector currently names', async function () {
            // Everything after resolution reads or launches asynchronously, and a selector is
            // only a name: it can be repointed while those operations run. The target therefore
            // carries the physical AppHost it resolved to, so an operation cannot be redirected
            // by a later retarget even though the selector itself still decides freshness.
            const linkedTarget = path.join(workspaceRoot, 'Linked', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(linkedTarget), { recursive: true });
            try {
                fs.symlinkSync(appHostProjectPath, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, linkedTarget);
            const resolution = await resolver.resolveTarget('Linked/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assertResolved(resolution);
            assert.strictEqual(resolution.target.absolutePath, linkedTarget);
            assert.strictEqual(resolution.target.canonicalPath, fs.realpathSync.native(appHostProjectPath));
            assert.strictEqual(
                resolver.getIdentityForAppHostPath(resolution.target.canonicalPath),
                resolution.target.identity);
            assert.strictEqual(resolution.target.displayPath, 'Linked/AppHost.csproj');
        });

        test('keeps a canonical bound path for a target the filesystem cannot canonicalize', async () => {
            // A registry entry can be removed between enumeration and use. The bound path then
            // falls back to the enumerated path so the target still names something, and the
            // identity check is what refuses to publish anything about it.
            const missingAppHost = path.join(workspaceRoot, 'Missing', 'AppHost.csproj');
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, missingAppHost);

            const resolution = await resolver.resolveTarget('Missing/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assertResolved(resolution);
            assert.strictEqual(resolution.target.canonicalPath, missingAppHost);
        });

        test('binds the source file of a project pair to the source file itself', async function () {
            // A project file and its sibling AppHost source share one identity, but they are two
            // different files. The bound path has to stay the entry the selector named, otherwise
            // a read or launch would be pointed at the sibling the caller did not select.
            const pairDirectory = path.join(workspaceRoot, 'Pair');
            const pairProject = path.join(pairDirectory, 'AppHost.csproj');
            const pairSource = path.join(pairDirectory, 'AppHost.cs');
            fs.mkdirSync(pairDirectory, { recursive: true });
            fs.writeFileSync(pairProject, appHostProjectContents);
            fs.writeFileSync(pairSource, '// AppHost');
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, pairSource);

            const resolution = await resolver.resolveTarget('Pair/AppHost.cs', new vscode.CancellationTokenSource().token);

            assertResolved(resolution);
            assert.strictEqual(resolution.target.canonicalPath, fs.realpathSync.native(pairSource));
        });

        test('keeps the canonical bound path out of every projection it hands a caller', async function () {
            // The target itself legitimately carries the physical path - that is what makes an
            // operation immune to a retarget. What must never carry it is anything a model or a
            // confirmation sees, because the physical path can name a file the caller never
            // chose a name for, and the identity a caller confirms is the workspace-relative one.
            const hiddenDirectory = path.join(workspaceRoot, 'Hidden-Physical-Location');
            const hiddenAppHost = path.join(hiddenDirectory, 'AppHost.csproj');
            const aliasAppHost = path.join(workspaceRoot, 'Alias', 'AppHost.csproj');
            fs.mkdirSync(hiddenDirectory, { recursive: true });
            fs.mkdirSync(path.dirname(aliasAppHost), { recursive: true });
            fs.writeFileSync(hiddenAppHost, appHostProjectContents);
            try {
                fs.symlinkSync(hiddenAppHost, aliasAppHost);
            }
            catch {
                this.skip();
                return;
            }

            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, aliasAppHost);
            const token = new vscode.CancellationTokenSource().token;

            const resolution = await resolver.resolveTarget('Alias/AppHost.csproj', token);
            const missing = await resolver.resolveTarget('Missing/AppHost.csproj', token);

            assertResolved(resolution);
            const canonicalPath = resolution.target.canonicalPath;
            assert.strictEqual(canonicalPath, fs.realpathSync.native(hiddenAppHost));
            // Everything a caller is allowed to show or return: the confirmation identity, the
            // workspace-relative path a tool result reports, and the recovery list a failed
            // resolution offers the model.
            const callerFacingProjections = JSON.stringify({
                confirmation: resolution.target.displayPath,
                result: resolution.target.relativePath,
                knownAppHosts: missing.resolved ? [] : missing.knownAppHosts,
            });

            assert.strictEqual(callerFacingProjections.includes(canonicalPath), false);
            assert.strictEqual(callerFacingProjections.includes('Hidden-Physical-Location'), false);
            assert.strictEqual(callerFacingProjections.includes(workspaceRoot), false);
            assert.deepStrictEqual(JSON.parse(callerFacingProjections), {
                confirmation: 'Alias/AppHost.csproj',
                result: 'Alias/AppHost.csproj',
                knownAppHosts: ['Alias/AppHost.csproj'],
            });
        });

        test('bounds known AppHosts on not-found results', async () => {
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            for (let index = 0; index < 40; index++) {
                const candidatePath = path.join(workspaceRoot, `Project${index.toString().padStart(2, '0')}`, 'AppHost.csproj');
                fs.mkdirSync(path.dirname(candidatePath), { recursive: true });
                fs.writeFileSync(candidatePath, appHostProjectContents);
                addCandidate(discoveryService, workspaceRoot, candidatePath);
            }

            const resolution = await resolver.resolveTarget('Missing/AppHost.csproj', new vscode.CancellationTokenSource().token);

            assert.strictEqual(resolution.resolved, false);
            if (resolution.resolved) {
                assert.fail('Expected a missing AppHost resolution.');
            }

            assert.strictEqual(resolution.outcome, 'appHostNotFound');
            assert.strictEqual(resolution.knownAppHosts?.length, 32);
            assert.strictEqual(JSON.stringify(resolution).includes(workspaceRoot), false);
        });
    });
});
