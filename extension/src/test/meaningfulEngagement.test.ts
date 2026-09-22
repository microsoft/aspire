import * as assert from 'assert';
import type { TelemetryReporter } from '@vscode/extension-telemetry';
import { existsSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import type { CandidateAppHostDisplayInfo, AppHostDiscoveryService } from '../utils/appHostDiscovery';
import * as appHostTargetVersion from '../utils/appHostTargetVersion';
import { MeaningfulEngagementReporter } from '../utils/meaningfulEngagement';
import { __resetCommonPropertiesForTests, __setReporterForTests, getCommonTelemetryProperties, sendTelemetryEvent, setCommonTelemetryProperties } from '../utils/telemetry';

import { createDeferred, removeDirectorySafely } from './testHelpers';

type DiscoveryResult = CandidateAppHostDisplayInfo[] | Error | Promise<CandidateAppHostDisplayInfo[]>;
interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        // Extension code now bypasses this path; recording here would only
        // see a regression to the prefixed channel. Kept as a typed no-op
        // so the fake still satisfies the TelemetryReporter shape.
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
    sendRawTelemetryEvent(): void { /* not used here */ }

    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('MeaningfulEngagementReporter', () => {
    let fake: FakeTelemetryReporter;
    let restoreReporter: () => void;
    const tempDirs: string[] = [];
    const tempParent = join(process.cwd(), '.test-tmp');

    function makeTempDir(): string {
        mkdirSync(tempParent, { recursive: true });
        const dir = mkdtempSync(join(tempParent, 'meaningful-engagement-'));
        tempDirs.push(dir);
        return dir;
    }

    setup(() => {
        fake = new FakeTelemetryReporter();
        restoreReporter = __setReporterForTests(fake as unknown as TelemetryReporter);
        __resetCommonPropertiesForTests();
    });

    teardown(() => {
        sinon.restore();
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                removeDirectorySafely(dir);
            }
        }
        tempDirs.length = 0;
        restoreReporter();
        __resetCommonPropertiesForTests();
    });

    suiteTeardown(() => {
        if (existsSync(tempParent)) {
            removeDirectorySafely(tempParent);
        }
    });

    test('includes AppHost target versions with AppHost language telemetry', async () => {
        const workspacePath = makeTempDir();
        const appHostPath = join(workspacePath, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
        const workspaceFolder = {
            uri: vscode.Uri.file(workspacePath),
            name: 'workspace',
            index: 0,
        } as vscode.WorkspaceFolder;
        const candidates: CandidateAppHostDisplayInfo[] = [{
            path: appHostPath,
            language: 'csharp',
            status: 'buildable',
        }];
        const discovery = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            discover: async () => candidates,
        } as unknown as AppHostDiscoveryService;
        sinon.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);

        const subscriptions: vscode.Disposable[] = [];
        const repository = new AppHostDataRepository(new AspireTerminalProvider(subscriptions), discovery);
        const reporter = new MeaningfulEngagementReporter(repository);
        try {
            await waitFor(() => fake.events.length === 1);

            assert.strictEqual(fake.events[0].name, 'aspire/vscode/engagement/active');
            assert.strictEqual(fake.events[0].properties?.trigger, 'apphost_detected');
            assert.strictEqual(fake.events[0].properties?.apphost_languages, 'csharp');
            assert.strictEqual(fake.events[0].properties?.apphost_target_versions, '13.5.0');
            assert.strictEqual(getCommonTelemetryProperties().apphost_target_versions, '13.5.0');
        }
        finally {
            reporter.dispose();
            repository.dispose();
            subscriptions.forEach(subscription => subscription.dispose());
        }
    });

    test('reports unknown workspace context when discovery fails at first engagement', async () => {
        const fixture = createDiscoveryFixture([new Error('Discovery failed')]);
        try {
            await waitFor(() => fixture.repository.hasError);
            fixture.reporter.recordCommandInvoked();
            await waitFor(() => fake.events.some(event => event.name === 'aspire/vscode/engagement/active'));

            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_languages: 'unknown',
                apphost_target_versions: 'unknown',
            });
        }
        finally {
            fixture.dispose();
        }
    });

    test('refreshes shared workspace context after discovery recovers without repeating engagement', async () => {
        const fixture = createDiscoveryFixture([new Error('Discovery failed')]);
        try {
            await waitFor(() => fixture.repository.hasError);
            fixture.reporter.recordCommandInvoked();
            await waitFor(() => fake.events.some(event => event.name === 'aspire/vscode/engagement/active'));

            fixture.setResult(fixture.folder, [fixture.candidate]);
            fixture.changed.fire(fixture.folder);
            await waitFor(() => fixture.repository.workspaceAppHostPath === fixture.candidate.path);
            await new Promise<void>(resolve => setImmediate(resolve));

            sendTelemetryEvent('aspire/vscode/command/invoked', {
                command: 'aspire-vscode.refreshAppHosts',
                outcome: 'success',
            });

            assert.deepStrictEqual(fake.events.at(-1)?.properties, {
                apphost_languages: 'csharp',
                apphost_target_versions: '13.5.0',
                apphost_present: 'true',
                command: 'aspire-vscode.refreshAppHosts',
                outcome: 'success',
            });
            assert.strictEqual(fake.events.filter(event => event.name === 'aspire/vscode/engagement/active').length, 1);
        }
        finally {
            fixture.dispose();
        }
    });

    test('records engagement during pending discovery without another discovery request', async () => {
        const pending = createDeferred<CandidateAppHostDisplayInfo[]>();
        const fixture = createDiscoveryFixture([pending.promise]);
        try {
            fixture.reporter.recordCommandInvoked();
            fixture.reporter.recordDebugSession();
            await waitFor(() => fake.events.length === 1);

            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_languages: 'unknown',
                apphost_target_versions: 'unknown',
            });
            assert.strictEqual(fake.events[0].properties?.trigger, 'command');
            assert.strictEqual(fixture.discover.callCount, 1);

            pending.resolve([fixture.candidate]);
            await waitFor(() => getCommonTelemetryProperties().apphost_target_versions === '13.5.0');
            assert.strictEqual(fake.events.length, 1);
            assert.strictEqual(fixture.discover.callCount, 1);
        }
        finally {
            fixture.dispose();
        }
    });

    test('refreshes confirmed absence when an AppHost is added and when all folders are removed', async () => {
        const fixture = createDiscoveryFixture([[]]);
        try {
            await waitFor(() => fixture.repository.isWorkspaceAppHostDiscoveryComplete);
            fixture.reporter.recordCommandInvoked();
            await waitFor(() => fake.events.length === 1);
            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_present: 'false',
                apphost_languages: 'none',
                apphost_target_versions: 'none',
            });

            fixture.setResult(fixture.folder, [fixture.candidate]);
            fixture.changed.fire(fixture.folder);
            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_languages: 'unknown',
                apphost_target_versions: 'unknown',
            });
            await waitFor(() => getCommonTelemetryProperties().apphost_target_versions === '13.5.0');
            assert.strictEqual(getCommonTelemetryProperties().apphost_present, 'true');

            const discoveryCalls = fixture.discover.callCount;
            fixture.setWorkspaceFolders([]);
            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_present: 'false',
                apphost_languages: 'none',
                apphost_target_versions: 'none',
            });
            assert.strictEqual(fixture.discover.callCount, discoveryCalls);
            assert.strictEqual(fake.events.length, 1);
        }
        finally {
            fixture.dispose();
        }
    });

    test('reports partial workspace context and refreshes after a non-first folder recovers', async () => {
        const fixture = createDiscoveryFixture([[], []]);
        try {
            await waitFor(() => fixture.repository.isWorkspaceAppHostDiscoveryComplete);
            fixture.reporter.recordCommandInvoked();
            await waitFor(() => fake.events.length === 1);
            const secondFolder = fixture.folders[1];
            const secondCandidate: CandidateAppHostDisplayInfo = {
                path: join(secondFolder.uri.fsPath, 'apphost.ts'),
                language: 'typescript',
                status: 'buildable',
            };
            fixture.targetVersions.callsFake(async candidates => candidates.length > 1 ? 'multiple' : '13.5.0');
            fixture.setResult(fixture.folder, [fixture.candidate]);
            fixture.setResult(secondFolder, new Error('Second folder failed'));
            fixture.changed.fire(secondFolder);
            await waitFor(() => fixture.repository.workspaceAppHostDiscovery.status === 'error');

            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_present: 'true',
                apphost_languages: 'unknown',
                apphost_target_versions: 'unknown',
            });
            assert.strictEqual(fixture.repository.hasError, false);
            assert.deepStrictEqual(fixture.repository.workspaceAppHostCandidatePaths, [fixture.candidate.path]);

            fixture.setResult(secondFolder, [secondCandidate]);
            fixture.changed.fire(secondFolder);
            await waitFor(() => getCommonTelemetryProperties().apphost_target_versions === 'multiple');
            assert.deepStrictEqual(getCommonTelemetryProperties(), {
                apphost_present: 'true',
                apphost_languages: 'polyglot',
                apphost_target_versions: 'multiple',
            });

            fixture.setWorkspaceFolders([secondFolder]);
            await waitFor(() => getCommonTelemetryProperties().apphost_target_versions === '13.5.0');
            assert.strictEqual(getCommonTelemetryProperties().apphost_languages, 'typescript');
            assert.strictEqual(fake.events.length, 1);
        }
        finally {
            fixture.dispose();
        }
    });

    test('ignores superseded version enrichment when the same AppHost is refreshed', async () => {
        const fixture = createDiscoveryFixture([[]]);
        const olderVersion = createDeferred<string>();
        try {
            await waitFor(() => fixture.repository.isWorkspaceAppHostDiscoveryComplete);
            fixture.targetVersions.onFirstCall().returns(olderVersion.promise);
            fixture.targetVersions.onSecondCall().resolves('13.6.0');
            fixture.setResult(fixture.folder, [fixture.candidate]);
            fixture.changed.fire(fixture.folder);
            await waitFor(() => fixture.targetVersions.callCount === 1);

            fixture.repository.refresh();
            await waitFor(() => getCommonTelemetryProperties().apphost_target_versions === '13.6.0');
            olderVersion.resolve('13.4.6');
            await waitFor(() => fake.events.length === 1);

            assert.strictEqual(getCommonTelemetryProperties().apphost_target_versions, '13.6.0');
            assert.strictEqual(fake.events[0].properties?.apphost_target_versions, '13.6.0');
        }
        finally {
            fixture.dispose();
        }
    });

    test('disposal prevents pending metadata from publishing and preserves unrelated common properties', async () => {
        const fixture = createDiscoveryFixture([[]]);
        const version = createDeferred<string>();
        try {
            await waitFor(() => fixture.repository.isWorkspaceAppHostDiscoveryComplete);
            setCommonTelemetryProperties({ is_microsoft_internal: 'true' });
            fixture.targetVersions.returns(version.promise);
            fixture.setResult(fixture.folder, [fixture.candidate]);
            fixture.changed.fire(fixture.folder);
            await waitFor(() => fixture.targetVersions.calledOnce);
            fixture.reporter.dispose();
            version.resolve('13.5.0');
            await new Promise<void>(resolve => setImmediate(resolve));
            fixture.reporter.recordCommandInvoked();
            fixture.changed.fire(fixture.folder);
            await new Promise<void>(resolve => setImmediate(resolve));

            assert.deepStrictEqual(getCommonTelemetryProperties(), { is_microsoft_internal: 'true' });
            assert.deepStrictEqual(fake.events, []);
        }
        finally {
            fixture.dispose();
        }
    });

    function createDiscoveryFixture(initialResults: readonly DiscoveryResult[]) {
        const folders: vscode.WorkspaceFolder[] = initialResults.map((_, index) => ({
            uri: vscode.Uri.file(makeTempDir()),
            name: `workspace-${index}`,
            index,
        }));
        const folder = folders[0];
        const candidate: CandidateAppHostDisplayInfo = {
            path: join(folder.uri.fsPath, 'AppHost.csproj'),
            language: 'csharp',
            status: 'buildable',
        };
        const changed = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        const workspaceChanged = new vscode.EventEmitter<vscode.WorkspaceFoldersChangeEvent>();
        const results = new Map(folders.map((currentFolder, index) => [currentFolder.uri.toString(), initialResults[index]]));
        const discover = sinon.stub().callsFake(async (currentFolder: vscode.WorkspaceFolder) => {
            const result = results.get(currentFolder.uri.toString());
            assert.ok(result, 'Unexpected workspace folder');
            if (result instanceof Error) {
                throw result;
            }
            return result;
        });
        const discovery = {
            onDidChangeCandidates: changed.event,
            discover,
        } as unknown as AppHostDiscoveryService;
        let currentFolders: readonly vscode.WorkspaceFolder[] = folders;
        sinon.stub(vscode.workspace, 'workspaceFolders').get(() => currentFolders);
        sinon.stub(vscode.workspace, 'onDidChangeWorkspaceFolders').callsFake(workspaceChanged.event);
        const targetVersions = sinon.stub(appHostTargetVersion, 'summarizeAppHostTargetVersions')
            .callsFake(async candidates => candidates.length > 0 ? '13.5.0' : 'none');
        const subscriptions: vscode.Disposable[] = [];
        const terminal = new AspireTerminalProvider(subscriptions);
        sinon.stub(terminal, 'getAspireCliExecutablePath').rejects(new Error('Telemetry must not invoke the CLI'));
        const repository = new AppHostDataRepository(terminal, discovery);
        const reporter = new MeaningfulEngagementReporter(repository);

        return {
            folder,
            folders,
            candidate,
            changed,
            discover,
            targetVersions,
            repository,
            reporter,
            setResult(currentFolder: vscode.WorkspaceFolder, result: DiscoveryResult) {
                results.set(currentFolder.uri.toString(), result);
            },
            setWorkspaceFolders(nextFolders: readonly vscode.WorkspaceFolder[]) {
                const removed = currentFolders.filter(currentFolder => !nextFolders.includes(currentFolder));
                const added = nextFolders.filter(currentFolder => !currentFolders.includes(currentFolder));
                currentFolders = nextFolders;
                workspaceChanged.fire({ added, removed });
            },
            dispose() {
                reporter.dispose();
                repository.dispose();
                changed.dispose();
                workspaceChanged.dispose();
                subscriptions.forEach(subscription => subscription.dispose());
            },
        };
    }
});

async function waitFor(predicate: () => boolean): Promise<void> {
    const start = Date.now();
    while (!predicate()) {
        if (Date.now() - start > 1000) {
            throw new Error('Timed out waiting for condition.');
        }

        await new Promise(resolve => setTimeout(resolve, 10));
    }
}
