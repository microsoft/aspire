import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AppHostsViewTelemetry } from '../views/AppHostsViewTelemetry';
import type { AppHostDataRepository, AppHostDisplayInfo, ResourceJson } from '../views/AppHostDataRepository';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

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

interface RepositoryState {
    appHosts: AppHostDisplayInfo[];
    workspaceResources: ResourceJson[];
    workspaceAppHostPath: string | undefined;
    workspaceAppHostCandidatePaths: string[];
    workspaceAppHost: AppHostDisplayInfo | undefined;
    isWorkspaceAppHostDiscoveryComplete: boolean;
    hasError: boolean;
}

suite('AppHostsViewTelemetry', () => {
    let sandbox: sinon.SinonSandbox;
    let reporter: FakeTelemetryReporter;
    let restoreReporter: () => void;
    let workspaceFolders: readonly vscode.WorkspaceFolder[] | undefined;

    setup(() => {
        sandbox = sinon.createSandbox();
        workspaceFolders = [{
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        }];
        sandbox.stub(vscode.workspace, 'workspaceFolders').get(() => workspaceFolders);
        reporter = new FakeTelemetryReporter();
        restoreReporter = __setReporterForTests(reporter as unknown as Parameters<typeof __setReporterForTests>[0]);
    });

    teardown(() => {
        restoreReporter();
        __resetCommonPropertiesForTests();
        sandbox.restore();
    });

    test('shown event reports workspace AppHost states', () => {
        const cases: Array<{
            name: string;
            workspaceOpen?: boolean;
            state: Partial<RepositoryState>;
            expectedState: string;
            expectedCandidateCount: number;
            expectedHasError: string;
        }> = [
            {
                name: 'no workspace',
                workspaceOpen: false,
                state: { workspaceAppHostCandidatePaths: ['/workspace/apphost.ts'] },
                expectedState: 'no_workspace',
                expectedCandidateCount: 1,
                expectedHasError: 'false',
            },
            {
                name: 'repository error',
                state: { hasError: true },
                expectedState: 'error',
                expectedCandidateCount: 0,
                expectedHasError: 'true',
            },
            {
                name: 'discovery in progress',
                state: { isWorkspaceAppHostDiscoveryComplete: false },
                expectedState: 'discovering',
                expectedCandidateCount: 0,
                expectedHasError: 'false',
            },
            {
                name: 'no candidates',
                state: { isWorkspaceAppHostDiscoveryComplete: true },
                expectedState: 'not_found',
                expectedCandidateCount: 0,
                expectedHasError: 'false',
            },
            {
                name: 'multiple candidates',
                state: {
                    isWorkspaceAppHostDiscoveryComplete: true,
                    workspaceAppHostCandidatePaths: ['/workspace/first.ts', '/workspace/second.ts'],
                },
                expectedState: 'multiple',
                expectedCandidateCount: 2,
                expectedHasError: 'false',
            },
            {
                name: 'selected candidate',
                state: {
                    isWorkspaceAppHostDiscoveryComplete: true,
                    workspaceAppHostCandidatePaths: ['/workspace/apphost.ts'],
                    workspaceAppHostPath: '/workspace/apphost.ts',
                },
                expectedState: 'selected',
                expectedCandidateCount: 1,
                expectedHasError: 'false',
            },
            {
                name: 'running candidate',
                state: {
                    appHosts: [makeAppHost()],
                    isWorkspaceAppHostDiscoveryComplete: true,
                    workspaceAppHostCandidatePaths: ['/workspace/apphost.ts'],
                    workspaceAppHostPath: '/workspace/apphost.ts',
                    workspaceAppHost: makeAppHost(),
                },
                expectedState: 'running',
                expectedCandidateCount: 1,
                expectedHasError: 'false',
            },
        ];

        for (const testCase of cases) {
            workspaceFolders = testCase.workspaceOpen === false ? undefined : [{
                uri: vscode.Uri.file('/workspace'),
                name: 'workspace',
                index: 0,
            }];
            reporter.events = [];

            const telemetry = new AppHostsViewTelemetry(makeTreeView(), makeRepository(testCase.state));
            try {
                fireShownEvent(telemetry);

                assert.strictEqual(reporter.events.length, 1, testCase.name);
                const event = reporter.events[0];
                assert.strictEqual(event.name, 'aspire/vscode/runningapphostsview/shown', testCase.name);
                assert.strictEqual(event.properties?.workspace_apphost_state, testCase.expectedState, testCase.name);
                assert.strictEqual(event.properties?.has_error, testCase.expectedHasError, testCase.name);
                assert.strictEqual(event.measurements?.workspace_apphost_candidates, testCase.expectedCandidateCount, testCase.name);
            }
            finally {
                telemetry.dispose();
            }
        }
    });
});

function fireShownEvent(telemetry: AppHostsViewTelemetry): void {
    (telemetry as unknown as { _fire(initial: boolean): void })._fire(false);
}

function makeTreeView(): vscode.TreeView<unknown> {
    return {
        visible: false,
        onDidChangeVisibility: () => new vscode.Disposable(() => { }),
    } as unknown as vscode.TreeView<unknown>;
}

function makeRepository(overrides: Partial<RepositoryState>): AppHostDataRepository {
    const state: RepositoryState = {
        appHosts: [],
        workspaceResources: [],
        workspaceAppHostPath: undefined,
        workspaceAppHostCandidatePaths: [],
        workspaceAppHost: undefined,
        isWorkspaceAppHostDiscoveryComplete: true,
        hasError: false,
        ...overrides,
    };

    return {
        viewMode: 'workspace',
        appHosts: state.appHosts,
        workspaceResources: state.workspaceResources,
        workspaceAppHostPath: state.workspaceAppHostPath,
        workspaceAppHostCandidatePaths: state.workspaceAppHostCandidatePaths,
        workspaceAppHost: state.workspaceAppHost,
        isWorkspaceAppHostDiscoveryComplete: state.isWorkspaceAppHostDiscoveryComplete,
        hasError: state.hasError,
    } as unknown as AppHostDataRepository;
}

function makeAppHost(overrides: Partial<AppHostDisplayInfo> = {}): AppHostDisplayInfo {
    return {
        appHostPath: '/workspace/apphost.ts',
        appHostPid: 1234,
        cliPid: null,
        dashboardUrl: null,
        resources: [],
        ...overrides,
    };
}
