import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';

import { EditorStateSnapshotService } from '../lm/editorStateSnapshotService';
import { SafeAppHostTargetResolver } from '../lm/safeAppHostTargetResolver';
import { getOrCreateIdentityForCurrentAppHostTarget } from '../utils/appHostIdentity';
import { __resetLaunchFailureJournalForTests } from '../services/launchFailureJournal';
import {
    __resetAppHostIdentityRegistryForTests,
    compareAppHostIdentity,
} from '../utils/appHostIdentity';
import {
    addCandidate,
    appHostProjectContents,
    assertResolved,
    createFixtureDirectory,
    createWorkspaceFolder,
    FakeDiscoveryService,
    FakeEditorStateLaunchService,
} from './helpers/editorAssistanceTestSupport';

/**
 * Matches the fail-closed rejection an undecidable running-AppHost relationship produces.
 *
 * The name rather than the class is asserted so these tests describe the observable contract
 * every editor-assistance surface maps to `ambiguousAppHost`.
 */
function isAmbiguousAppHostOwnership(error: unknown): boolean {
    return error instanceof Error && error.name === 'AmbiguousAppHostOwnershipError';
}

/**
 * Matches the fail-closed rejection a target that no longer names its resolved entry produces.
 */
function isStaleAppHostTarget(error: unknown): boolean {
    return error instanceof Error && error.name === 'StaleAppHostTargetError';
}

suite('Editor assistance AppHost services', () => {
    let workspaceRoot: string;
    let outsideRoot: string;
    let workspaceFoldersStub: sinon.SinonStub;
    let discoveryService: FakeDiscoveryService;
    let launchService: FakeEditorStateLaunchService;
    let resolver: SafeAppHostTargetResolver;
    let snapshotService: EditorStateSnapshotService;
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
        launchService = new FakeEditorStateLaunchService();
        resolver = new SafeAppHostTargetResolver(discoveryService);
        snapshotService = new EditorStateSnapshotService({
            launchService,
            targetResolver: resolver,
        });
    });

    teardown(() => {
        __resetLaunchFailureJournalForTests();
        __resetAppHostIdentityRegistryForTests();
        workspaceFoldersStub.restore();
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
        fs.rmSync(outsideRoot, { recursive: true, force: true });
    });

    suite('EditorStateSnapshotService', () => {
        test('uses the same projection for full, active, and exact summaries', async () => {
            const otherAppHostSourcePath = path.join(path.dirname(appHostProjectPath), 'apphost.cs');
            fs.writeFileSync(otherAppHostSourcePath, 'var builder = DistributedApplication.CreateBuilder(args);');
            const activeAppHostPath = path.join(workspaceRoot, 'ActiveAppHost', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(activeAppHostPath), { recursive: true });
            fs.writeFileSync(activeAppHostPath, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, activeAppHostPath);
            launchService.editorSessions.push(
                {
                    appHostPath: otherAppHostSourcePath,
                    resolvedAppHostPath: activeAppHostPath,
                    operationKind: 'run',
                    startupCompleted: true,
                    noDebug: false,
                    isStopping: false,
                },
                {
                    appHostPath: activeAppHostPath,
                    resolvedAppHostPath: activeAppHostPath,
                    operationKind: 'deploy',
                    startupCompleted: false,
                    noDebug: true,
                    isStopping: true,
                });

            const token = new vscode.CancellationTokenSource().token;
            const resolution = await resolver.resolveTarget('ActiveAppHost/AppHost.csproj', token);
            assertResolved(resolution);
            assert.strictEqual(compareAppHostIdentity(otherAppHostSourcePath, appHostProjectPath), 'same');
            assert.strictEqual(compareAppHostIdentity(otherAppHostSourcePath, activeAppHostPath), 'different');

            const snapshot = await snapshotService.createSnapshot(token);
            const activeSnapshot = await snapshotService.createActiveSessionSnapshot(token);
            const exactSummary = await snapshotService.getAppHostSummary(resolution.target, token);
            const snapshotSummary = snapshot.appHosts.find(summary => summary.appHost === resolution.target.displayPath);
            const activeEntry = activeSnapshot.appHosts.find(entry => entry.summary.appHost === resolution.target.displayPath);
            const expectedSummary = {
                appHost: 'ActiveAppHost/AppHost.csproj',
                state: 'running',
                mode: 'debug',
                controller: 'editor',
            };

            assert.strictEqual(snapshot.appHosts.length, 2);
            assert.strictEqual(activeSnapshot.appHosts.length, 1);
            assert.deepStrictEqual(snapshotSummary, expectedSummary);
            assert.deepStrictEqual(activeEntry?.summary, expectedSummary);
            // The resolved target the summary came from is carried alongside it so callers can
            // read each AppHost without re-resolving its display path.
            assert.strictEqual(activeEntry?.target.identity, resolution.target.identity);
            assert.deepStrictEqual(exactSummary, expectedSummary);
        });

        test('reports notDebugging when a known AppHost has no active editor session', async () => {
            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot, {
                appHosts: [{
                    appHost: 'AppHost/AppHost.csproj',
                    state: 'notDebugging',
                    mode: 'other',
                    controller: 'editor',
                }],
            });
        });

        test('reports an externally running AppHost only in full and exact summaries', async () => {
            const idleAppHostPath = path.join(workspaceRoot, 'IdleAppHost', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(idleAppHostPath), { recursive: true });
            fs.writeFileSync(idleAppHostPath, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, idleAppHostPath);
            launchService.runningAppHosts.push({ appHostPath: appHostProjectPath });

            const token = new vscode.CancellationTokenSource().token;
            const resolution = await resolver.resolveTarget('AppHost/AppHost.csproj', token);
            assertResolved(resolution);

            const snapshot = await snapshotService.createSnapshot(token);
            assert.strictEqual(launchService.runningAppHostRequests, 1);
            const activeSnapshot = await snapshotService.createActiveSessionSnapshot(token);
            assert.strictEqual(launchService.runningAppHostRequests, 1);
            const exactSummary = await snapshotService.getAppHostSummary(resolution.target, token);
            assert.strictEqual(launchService.runningAppHostRequests, 2);

            const expectedExternalSummary = {
                appHost: 'AppHost/AppHost.csproj',
                state: 'running',
                mode: 'other',
                controller: 'external',
            };
            assert.deepStrictEqual(snapshot.appHosts, [
                expectedExternalSummary,
                {
                    appHost: 'IdleAppHost/AppHost.csproj',
                    state: 'notDebugging',
                    mode: 'other',
                    controller: 'editor',
                },
            ]);
            assert.deepStrictEqual(
                activeSnapshot.appHosts.map(entry => entry.summary),
                []);
            assert.deepStrictEqual(
                activeSnapshot.appHosts.map(entry => entry.target.identity),
                []);
            assert.strictEqual(Object.prototype.hasOwnProperty.call(activeSnapshot, 'truncated'), false);
            assert.deepStrictEqual(exactSummary, expectedExternalSummary);
            // Only the active AppHost is summarized, but the idle one is still carried out so the
            // caller's freshness barrier covers the scope this answer was formed over.
            assert.deepStrictEqual(
                activeSnapshot.observedTargets.map(target => target.displayPath),
                ['AppHost/AppHost.csproj', 'IdleAppHost/AppHost.csproj']);
        });

        test('fails closed when a running AppHost relationship is ambiguous', async () => {
            // `ambiguous` means the running row may or may not be this AppHost. Reporting it as
            // externally running claims ownership that was never established, so every summary
            // surface refuses the same way `EditorUiHandoffService` does.
            const ambiguousDirectory = path.join(workspaceRoot, 'AmbiguousExternal');
            const firstProject = path.join(ambiguousDirectory, 'First.csproj');
            const secondProject = path.join(ambiguousDirectory, 'Second.csproj');
            const appHostSource = path.join(ambiguousDirectory, 'Program.cs');
            fs.mkdirSync(ambiguousDirectory, { recursive: true });
            fs.writeFileSync(firstProject, appHostProjectContents);
            fs.writeFileSync(secondProject, appHostProjectContents);
            fs.writeFileSync(appHostSource, 'var builder = DistributedApplication.CreateBuilder(args);');
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, appHostSource);
            launchService.runningAppHosts.push({ appHostPath: firstProject });
            assert.strictEqual(compareAppHostIdentity(appHostSource, firstProject), 'ambiguous');

            const token = new vscode.CancellationTokenSource().token;
            const resolution = await resolver.resolveTarget('AmbiguousExternal/Program.cs', token);
            assertResolved(resolution);

            await assert.rejects(
                () => snapshotService.createSnapshot(token),
                isAmbiguousAppHostOwnership);
            assert.deepStrictEqual(await snapshotService.createActiveSessionSnapshot(token), {
                appHosts: [],
                observedTargets: [resolution.target],
            });
            await assert.rejects(
                () => snapshotService.getAppHostSummary(resolution.target, token),
                isAmbiguousAppHostOwnership);
        });

        test('reports external ownership through symlinked and project-source equivalent running paths', async function () {
            // The running registry reports whichever path the CLI was started with. An alias that
            // reaches the same file, and the sibling source file of a single project/source pair,
            // are both the same AppHost, so neither may be reported as idle.
            const linkedRunningPath = path.join(workspaceRoot, 'AppHost', 'Linked.csproj');
            try {
                fs.symlinkSync(appHostProjectPath, linkedRunningPath);
            }
            catch {
                this.skip();
                return;
            }

            const pairDirectory = path.join(workspaceRoot, 'ZPair');
            const pairProject = path.join(pairDirectory, 'AppHost.csproj');
            const pairSource = path.join(pairDirectory, 'apphost.cs');
            fs.mkdirSync(pairDirectory, { recursive: true });
            fs.writeFileSync(pairProject, appHostProjectContents);
            fs.writeFileSync(pairSource, 'var builder = DistributedApplication.CreateBuilder(args);');
            addCandidate(discoveryService, workspaceRoot, pairProject);
            launchService.runningAppHosts.push(
                { appHostPath: linkedRunningPath },
                { appHostPath: pairSource });
            assert.strictEqual(compareAppHostIdentity(appHostProjectPath, linkedRunningPath), 'same');
            assert.strictEqual(compareAppHostIdentity(pairProject, pairSource), 'same');

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [
                {
                    appHost: 'AppHost/AppHost.csproj',
                    state: 'running',
                    mode: 'other',
                    controller: 'external',
                },
                {
                    appHost: 'ZPair/AppHost.csproj',
                    state: 'running',
                    mode: 'other',
                    controller: 'external',
                },
            ]);
        });

        test('reports an unrelated running AppHost as idle rather than externally owned', async () => {
            const otherAppHostPath = path.join(workspaceRoot, 'Other', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(otherAppHostPath), { recursive: true });
            fs.writeFileSync(otherAppHostPath, appHostProjectContents);
            launchService.runningAppHosts.push({ appHostPath: otherAppHostPath });
            assert.strictEqual(compareAppHostIdentity(appHostProjectPath, otherAppHostPath), 'different');

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'notDebugging',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('reports an editor-owned pending run when the running registry also observes it', async () => {
            launchService.launchingPaths.add(path.resolve(appHostProjectPath));
            launchService.pendingOrActiveRunLaunchPaths.add(path.resolve(appHostProjectPath));
            launchService.runningAppHosts.push({ appHostPath: appHostProjectPath });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'starting',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('does not report starting from a non-run launch reservation', async () => {
            launchService.launchingPaths.add(path.resolve(appHostProjectPath));

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'notDebugging',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('reports an editor-owned session when the running registry also observes it', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });
            launchService.runningAppHosts.push({ appHostPath: appHostProjectPath });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'running',
                mode: 'debug',
                controller: 'editor',
            }]);
        });

        test('reports a starting run session before startup completes', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: false,
                noDebug: true,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'starting',
                mode: 'run',
                controller: 'editor',
            }]);
        });

        test('uses other mode for a run session with malformed debug configuration', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: undefined,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'running',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('ignores non-run editor sessions', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'publish',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'notDebugging',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('reports stopping when the matching editor session is shutting down', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: true,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'stopping',
                mode: 'debug',
                controller: 'editor',
            }]);
        });

        test('reports multipleSessions when more than one run session maps to the same AppHost', async () => {
            launchService.editorSessions.push(
                {
                    appHostPath: appHostProjectPath,
                    resolvedAppHostPath: appHostProjectPath,
                    operationKind: 'run',
                    startupCompleted: true,
                    noDebug: false,
                    isStopping: false,
                },
                {
                    appHostPath: appHostProjectPath,
                    resolvedAppHostPath: appHostProjectPath,
                    operationKind: 'run',
                    startupCompleted: true,
                    noDebug: false,
                    isStopping: false,
                });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'multipleSessions',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('omits editor sessions that cannot be resolved back to a known AppHost', async () => {
            const externalSessionPath = path.join(outsideRoot, 'External', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(externalSessionPath), { recursive: true });
            fs.writeFileSync(externalSessionPath, appHostProjectContents);
            launchService.editorSessions.push({
                appHostPath: externalSessionPath,
                resolvedAppHostPath: externalSessionPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'notDebugging',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('does not attribute an ambiguous run session to a known AppHost', async () => {
            const ambiguousDirectory = path.join(workspaceRoot, 'Ambiguous');
            const firstProject = path.join(ambiguousDirectory, 'First.csproj');
            const secondProject = path.join(ambiguousDirectory, 'Second.csproj');
            const appHostSource = path.join(ambiguousDirectory, 'Program.cs');
            fs.mkdirSync(ambiguousDirectory, { recursive: true });
            fs.writeFileSync(firstProject, appHostProjectContents);
            fs.writeFileSync(secondProject, appHostProjectContents);
            fs.writeFileSync(appHostSource, 'var builder = DistributedApplication.CreateBuilder(args);');
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, appHostSource);
            launchService.editorSessions.push({
                appHostPath: firstProject,
                resolvedAppHostPath: undefined,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [{
                appHost: 'Ambiguous/Program.cs',
                state: 'notDebugging',
                mode: 'other',
                controller: 'editor',
            }]);
        });

        test('keeps an active session attributed to its launch target after a symlink retargets', async function () {
            const firstTarget = path.join(workspaceRoot, 'First', 'AppHost.csproj');
            const secondTarget = path.join(workspaceRoot, 'Second', 'AppHost.csproj');
            const linkedTarget = path.join(workspaceRoot, 'ZLinked', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(firstTarget), { recursive: true });
            fs.mkdirSync(path.dirname(secondTarget), { recursive: true });
            fs.mkdirSync(path.dirname(linkedTarget), { recursive: true });
            fs.writeFileSync(firstTarget, appHostProjectContents);
            fs.writeFileSync(secondTarget, appHostProjectContents);
            try {
                fs.symlinkSync(firstTarget, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, firstTarget);
            addCandidate(discoveryService, workspaceRoot, secondTarget);
            addCandidate(discoveryService, workspaceRoot, linkedTarget);
            launchService.editorSessions.push({
                appHostPath: linkedTarget,
                resolvedAppHostPath: linkedTarget,
                appHostIdentity: getOrCreateIdentityForCurrentAppHostTarget(linkedTarget),
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            fs.rmSync(linkedTarget);
            fs.symlinkSync(secondTarget, linkedTarget);

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts, [
                {
                    appHost: 'First/AppHost.csproj',
                    state: 'running',
                    mode: 'debug',
                    controller: 'editor',
                },
                {
                    appHost: 'Second/AppHost.csproj',
                    state: 'notDebugging',
                    mode: 'other',
                    controller: 'editor',
                },
            ]);
        });

        test('fails full and exact summaries closed when a captured AppHost retargets during the running registry read', async function () {
            // The running-AppHost read is the one asynchronous step between capturing the
            // targets and publishing their states, so an alias can be repointed across it. Every
            // captured target is revalidated before publication: an entry whose file changed can
            // neither be reported nor quietly left out of an "everything active" answer.
            const firstTarget = path.join(workspaceRoot, 'First', 'AppHost.csproj');
            const secondTarget = path.join(workspaceRoot, 'Second', 'AppHost.csproj');
            const linkedTarget = path.join(workspaceRoot, 'ALinked', 'AppHost.csproj');
            for (const target of [firstTarget, secondTarget, linkedTarget]) {
                fs.mkdirSync(path.dirname(target), { recursive: true });
            }

            fs.writeFileSync(firstTarget, appHostProjectContents);
            fs.writeFileSync(secondTarget, appHostProjectContents);
            try {
                fs.symlinkSync(firstTarget, linkedTarget);
            }
            catch {
                this.skip();
                return;
            }

            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            addCandidate(discoveryService, workspaceRoot, linkedTarget);
            addCandidate(discoveryService, workspaceRoot, secondTarget);
            // Both AppHosts are externally running, so both are aggregate results the retarget
            // could corrupt rather than a single entry that could simply be refused.
            launchService.runningAppHosts.push(
                { appHostPath: firstTarget },
                { appHostPath: secondTarget });
            // The retarget is driven by the read itself, so the race is reproduced on every run
            // without a timer.
            launchService.beforeGetRunningAppHosts = () => {
                fs.rmSync(linkedTarget);
                fs.symlinkSync(secondTarget, linkedTarget);
            };
            const restoreLink = () => {
                fs.rmSync(linkedTarget);
                fs.symlinkSync(firstTarget, linkedTarget);
                __resetAppHostIdentityRegistryForTests();
            };
            const token = new vscode.CancellationTokenSource().token;

            await assert.rejects(() => snapshotService.createSnapshot(token), isStaleAppHostTarget);
            assert.strictEqual(launchService.runningAppHostRequests, 1);

            restoreLink();
            const activeSnapshot = await snapshotService.createActiveSessionSnapshot(token);
            assert.deepStrictEqual(activeSnapshot.appHosts, []);
            assert.deepStrictEqual(
                activeSnapshot.observedTargets.map(target => target.displayPath),
                ['ALinked/AppHost.csproj', 'Second/AppHost.csproj']);
            assert.strictEqual(launchService.runningAppHostRequests, 1);

            const resolution = await resolver.resolveTarget('ALinked/AppHost.csproj', token);
            assertResolved(resolution);
            await assert.rejects(
                () => snapshotService.getAppHostSummary(resolution.target, token),
                isStaleAppHostTarget);
            assert.strictEqual(launchService.runningAppHostRequests, 2);
        });

        test('answers editor-known AppHost state without reading the running registry', async () => {
            // `aspire ps` is a live CLI call that can fail or time out. It only decides whether
            // something outside this window runs an AppHost, so an answer this window already
            // knows must not depend on it.
            const startingAppHostPath = path.join(workspaceRoot, 'Starting', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(startingAppHostPath), { recursive: true });
            fs.writeFileSync(startingAppHostPath, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, startingAppHostPath);
            launchService.pendingOrActiveRunLaunchPaths.add(path.resolve(startingAppHostPath));
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });
            launchService.beforeGetRunningAppHosts = () => {
                throw new Error('aspire ps is unavailable');
            };

            const token = new vscode.CancellationTokenSource().token;
            const resolution = await resolver.resolveTarget('AppHost/AppHost.csproj', token);
            assertResolved(resolution);
            const snapshot = await snapshotService.createSnapshot(token);
            const activeSnapshot = await snapshotService.createActiveSessionSnapshot(token);
            const exactSummary = await snapshotService.getAppHostSummary(resolution.target, token);

            const expectedSummaries = [
                {
                    appHost: 'AppHost/AppHost.csproj',
                    state: 'running',
                    mode: 'debug',
                    controller: 'editor',
                },
                {
                    appHost: 'Starting/AppHost.csproj',
                    state: 'starting',
                    mode: 'other',
                    controller: 'editor',
                },
            ];
            assert.deepStrictEqual(snapshot.appHosts, expectedSummaries);
            assert.deepStrictEqual(activeSnapshot.appHosts.map(entry => entry.summary), expectedSummaries);
            assert.deepStrictEqual(exactSummary, expectedSummaries[0]);
            assert.strictEqual(launchService.runningAppHostRequests, 0);
        });

        test('lists active editor sessions without reading external ownership for idle AppHosts', async () => {
            const idleAppHostPath = path.join(workspaceRoot, 'Idle', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(idleAppHostPath), { recursive: true });
            fs.writeFileSync(idleAppHostPath, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, idleAppHostPath);
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });
            launchService.beforeGetRunningAppHosts = () => {
                throw new Error('aspire ps is unavailable');
            };

            const snapshot = await snapshotService.createActiveSessionSnapshot(new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(snapshot.appHosts.map(entry => entry.summary), [{
                appHost: 'AppHost/AppHost.csproj',
                state: 'running',
                mode: 'debug',
                controller: 'editor',
            }]);
            assert.deepStrictEqual(
                snapshot.observedTargets.map(target => target.displayPath),
                ['AppHost/AppHost.csproj', 'Idle/AppHost.csproj']);
            assert.strictEqual(launchService.runningAppHostRequests, 0);
        });

        test('fails closed when external ownership is required and cannot be read', async () => {
            // Nothing in this window claims the AppHost, so only the running registry could say
            // whether something else does. A failed read is not "nothing is running it".
            launchService.beforeGetRunningAppHosts = () => {
                throw new Error('aspire ps is unavailable');
            };
            const token = new vscode.CancellationTokenSource().token;
            const resolution = await resolver.resolveTarget('AppHost/AppHost.csproj', token);
            assertResolved(resolution);

            await assert.rejects(() => snapshotService.createSnapshot(token), /aspire ps is unavailable/);
            assert.deepStrictEqual(await snapshotService.createActiveSessionSnapshot(token), {
                appHosts: [],
                observedTargets: [resolution.target],
            });
            await assert.rejects(
                () => snapshotService.getAppHostSummary(resolution.target, token),
                /aspire ps is unavailable/);
            assert.strictEqual(launchService.runningAppHostRequests, 2);
        });

        test('returns at most 20 AppHosts sorted by safe display path', async () => {
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            for (let index = 20; index >= 0; index--) {
                const candidatePath = path.join(workspaceRoot, `Project${index.toString().padStart(2, '0')}`, 'AppHost.csproj');
                fs.mkdirSync(path.dirname(candidatePath), { recursive: true });
                fs.writeFileSync(candidatePath, appHostProjectContents);
                addCandidate(discoveryService, workspaceRoot, candidatePath);
            }

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);
            const appHosts = snapshot.appHosts.map(summary => summary.appHost);

            assert.strictEqual(appHosts.length, 20);
            assert.deepStrictEqual(appHosts, Array.from({ length: 20 }, (_, index) => `Project${index.toString().padStart(2, '0')}/AppHost.csproj`));
        });

        test('gets the exact requested AppHost summary beyond the bounded list snapshot', async () => {
            discoveryService.candidatesByFolder.set(workspaceRoot, []);
            for (let index = 0; index < 20; index++) {
                const candidatePath = path.join(workspaceRoot, `Project${index.toString().padStart(2, '0')}`, 'AppHost.csproj');
                fs.mkdirSync(path.dirname(candidatePath), { recursive: true });
                fs.writeFileSync(candidatePath, appHostProjectContents);
                addCandidate(discoveryService, workspaceRoot, candidatePath);
            }

            const exactAppHostPath = path.join(workspaceRoot, 'ZExact', 'AppHost.csproj');
            fs.mkdirSync(path.dirname(exactAppHostPath), { recursive: true });
            fs.writeFileSync(exactAppHostPath, appHostProjectContents);
            addCandidate(discoveryService, workspaceRoot, exactAppHostPath);
            launchService.editorSessions.push({
                appHostPath: exactAppHostPath,
                resolvedAppHostPath: exactAppHostPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            const resolution = await resolver.resolveTarget('ZExact/AppHost.csproj', new vscode.CancellationTokenSource().token);
            assertResolved(resolution);

            const summary = await snapshotService.getAppHostSummary(resolution.target, new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(summary, {
                appHost: 'ZExact/AppHost.csproj',
                state: 'running',
                mode: 'debug',
                controller: 'editor',
            });
        });

        test('returns summaries with only safe AppHost state fields', async () => {
            launchService.editorSessions.push({
                appHostPath: appHostProjectPath,
                resolvedAppHostPath: appHostProjectPath,
                operationKind: 'run',
                startupCompleted: true,
                noDebug: false,
                isStopping: false,
            });

            const snapshot = await snapshotService.createSnapshot(new vscode.CancellationTokenSource().token);
            const [summary] = snapshot.appHosts;

            assert.deepStrictEqual(Object.keys(snapshot), ['appHosts']);
            assert.deepStrictEqual(Object.keys(summary).sort(), ['appHost', 'controller', 'mode', 'state']);
            assert.strictEqual(JSON.stringify(snapshot).includes(workspaceRoot), false);
        });
    });
});
