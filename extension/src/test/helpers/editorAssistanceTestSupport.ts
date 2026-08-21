import * as assert from 'assert';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

import { type AspireOperationKind } from '../../dcp/types';
import {
    type AppHostEditorStateLaunchService,
    type AppHostLifecycleDiscoveryService,
    type AppHostLifecycleEditorSessions,
    type AppHostLifecycleRunningAppHost,
} from '../../lm/appHostLifecycleToolContracts';
import { type CandidateAppHostDisplayInfo } from '../../utils/appHostDiscovery';
import { compareAppHostIdentity, type OpaqueAppHostIdentity } from '../../utils/appHostIdentity';

interface TestEditorSession {
    readonly appHostPath: string | undefined;
    readonly resolvedAppHostPath: string | undefined;
    readonly appHostIdentity?: OpaqueAppHostIdentity;
    readonly operationKind: AspireOperationKind;
    readonly startupCompleted: boolean;
    readonly noDebug: boolean | undefined;
    readonly isStopping: boolean;
}

class FakeDiscoveryService implements AppHostLifecycleDiscoveryService {
    readonly candidatesByFolder = new Map<string, CandidateAppHostDisplayInfo[]>();
    readonly discoverErrorsByFolder = new Map<string, Error>();
    discoverCalls = 0;
    discoverError: Error | undefined;
    /**
     * Runs once a `discover` call has produced its candidates.
     *
     * Discovery is the asynchronous step every snapshot crosses, so mutating the registry from
     * here reproduces an AppHost joining or leaving it between the snapshot and everything that
     * reads afterwards, deterministically and without a timer.
     */
    afterDiscover: ((workspaceFolder: vscode.WorkspaceFolder) => void) | undefined;

    async discover(workspaceFolder: vscode.WorkspaceFolder, _forceRefresh?: boolean, cancellationToken?: vscode.CancellationToken): Promise<readonly CandidateAppHostDisplayInfo[]> {
        this.discoverCalls++;
        if (cancellationToken?.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        if (this.discoverError) {
            throw this.discoverError;
        }

        const folderPath = fs.realpathSync.native(workspaceFolder.uri.fsPath);
        const folderError = this.discoverErrorsByFolder.get(folderPath);
        if (folderError) {
            throw folderError;
        }

        // Read before the hook runs so a hook that replaces the registry entry cannot retroactively
        // change what this call already enumerated.
        const candidates = this.candidatesByFolder.get(folderPath) ?? [];
        this.afterDiscover?.(workspaceFolder);
        return candidates;
    }
}

class FakeEditorStateLaunchService implements AppHostEditorStateLaunchService {
    readonly launchingPaths = new Set<string>();
    readonly pendingOrActiveRunLaunchPaths = new Set<string>();
    readonly editorSessions: TestEditorSession[] = [];
    readonly runningAppHosts: AppHostLifecycleRunningAppHost[] = [];
    runningAppHostRequests = 0;
    /**
     * Runs while a running-AppHost read is in flight.
     *
     * Callers use it to mutate the workspace at the exact point a caller is awaiting this
     * service, so a race between resolution and publication is reproduced by the read itself
     * rather than by a timer.
     */
    beforeGetRunningAppHosts: (() => Promise<void> | void) | undefined;

    isLaunching(appHostPath: string): boolean {
        return this.launchingPaths.has(path.resolve(appHostPath));
    }

    hasPendingOrActiveRunLaunch(appHostPath: string): boolean {
        return this.pendingOrActiveRunLaunchPaths.has(path.resolve(appHostPath));
    }

    getEditorRunSessions(appHostPath: string): AppHostLifecycleEditorSessions {
        const sessions = [] as Array<{
            appHostPath: string | undefined;
            startupCompleted: boolean;
            configuration: { noDebug?: boolean; command?: string };
            stopDebugging(): Promise<void>;
        }>;
        let ambiguous = false;
        for (const session of this.editorSessions) {
            if (session.operationKind !== 'run') {
                continue;
            }

            switch (compareAppHostIdentity(session.resolvedAppHostPath ?? session.appHostPath, appHostPath)) {
                case 'same':
                    sessions.push({
                        appHostPath: session.appHostPath,
                        startupCompleted: session.startupCompleted,
                        configuration: { noDebug: session.noDebug, command: session.operationKind },
                        stopDebugging: async () => { },
                    });
                    break;
                case 'ambiguous':
                    ambiguous = true;
                    break;
            }
        }

        return { sessions, ambiguous };
    }

    getEditorSessions(): readonly TestEditorSession[] {
        return this.editorSessions;
    }

    async getRunningAppHosts(token: vscode.CancellationToken): Promise<readonly AppHostLifecycleRunningAppHost[]> {
        this.runningAppHostRequests++;
        if (token.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        await this.beforeGetRunningAppHosts?.();
        return this.runningAppHosts;
    }
}

const appHostProjectContents = `<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.0.0" />
</Project>`;

function createFixtureDirectory(prefix: string): string {
    const fixtureRoot = path.resolve(__dirname, '..', '..', '..', '.test-workspace', 'editor-assistance');
    const directory = path.join(fixtureRoot, `${prefix}-${crypto.randomBytes(6).toString('hex')}`);
    fs.mkdirSync(directory, { recursive: true });
    return fs.realpathSync.native(directory);
}

function createWorkspaceFolder(root: string, name: string, index: number): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(root),
        name,
        index,
    };
}

function addCandidate(discoveryService: FakeDiscoveryService, folderRoot: string, candidatePath: string): void {
    const existing = discoveryService.candidatesByFolder.get(folderRoot) ?? [];
    existing.push({ path: candidatePath, language: 'csharp', status: 'buildable' });
    discoveryService.candidatesByFolder.set(folderRoot, existing);
}

function assertResolved<T extends { resolved: boolean }>(resolution: T): asserts resolution is T & { resolved: true; target: { absolutePath: string; canonicalPath: string; relativePath: string; displayPath: string; identity: string } } {
    assert.strictEqual(resolution.resolved, true, `Expected a resolved target but got ${JSON.stringify(resolution)}`);
}

// Minimal `vscode.WorkspaceConfiguration` fake for the `aspire` configuration section. Callers under
// test (dashboardLauncher.ts's resolvers, editor-assistance tools) only call `get`/`inspect`/`has`,
// so those are the only members that need real behavior; `update` is a no-op.
function createAspireConfiguration(values: Readonly<Record<string, unknown>> = {}): vscode.WorkspaceConfiguration {
    return {
        get<T>(section: string, defaultValue?: T): T | undefined {
            return Object.prototype.hasOwnProperty.call(values, section)
                ? values[section] as T
                : defaultValue;
        },
        inspect<T>(section: string): {
            key: string;
            defaultValue?: T;
            globalValue?: T;
            workspaceValue?: T;
            workspaceFolderValue?: T;
            defaultLanguageValue?: T;
            globalLanguageValue?: T;
            workspaceLanguageValue?: T;
            workspaceFolderLanguageValue?: T;
            languageIds?: string[];
        } | undefined {
            if (!Object.prototype.hasOwnProperty.call(values, section)) {
                return undefined;
            }

            return {
                key: section,
                globalValue: values[section] as T,
            };
        },
        has(section: string): boolean {
            return Object.prototype.hasOwnProperty.call(values, section);
        },
        update: async () => { },
    };
}

export {
    addCandidate,
    appHostProjectContents,
    assertResolved,
    createAspireConfiguration,
    createFixtureDirectory,
    createWorkspaceFolder,
    FakeDiscoveryService,
    FakeEditorStateLaunchService,
    type TestEditorSession,
};
