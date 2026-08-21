import * as assert from 'assert';

import {
    aspireDebugSessionStatusToolName,
    aspireExplainLaunchFailureToolName,
    aspireHotReloadStatusToolName,
    aspireListDebugSessionsToolName,
    aspireOpenDashboardToolName,
    aspireOpenOutputToolName,
    type EditorAssistanceToolResult,
} from '../lm/editorAssistanceToolContracts';
import {
    EditorAssistanceTelemetry,
    type EditorAssistanceTelemetryEvent,
} from '../lm/editorAssistanceTelemetry';
import {
    launchFailureCategories,
    launchFailureControllers,
    launchFailureExitCodeBuckets,
    launchFailureModes,
    launchFailureProviderKinds,
    launchFailureStages,
    normalizeLaunchFailure,
    type LaunchFailureInput,
    type SanitizedLaunchFailure,
} from '../services/launchFailureJournal';

const expectedLaunchFailureStages = [
    'discovery',
    'validation',
    'cliLaunch',
    'build',
    'dcpStartup',
    'debugSession',
    'dashboard',
] as const;
const expectedLaunchFailureCategories = [
    'invalidConfiguration',
    'missingDependency',
    'cliUnavailable',
    'buildFailed',
    'processExited',
    'timeout',
    'portConflict',
    'permissionDenied',
    'unsupported',
    'canceled',
    'unknown',
] as const;
const expectedLaunchFailureControllers = ['editor', 'cli'] as const;
const expectedLaunchFailureModes = ['run', 'debug', 'deploy', 'publish', 'other'] as const;
const expectedLaunchFailureProviderKinds = [
    'dotnet',
    'node',
    'python',
    'java',
    'go',
    'rust',
    'maui',
    'azureFunctions',
    'browser',
    'bun',
    'other',
] as const;
const expectedLaunchFailureExitCodeCases = [
    ['none', {}],
    ['zero', { exitCode: 0 }],
    ['one', { exitCode: 1 }],
    ['signal', { signal: 'SIGTERM' }],
    ['other', { exitCode: 2 }],
] as const;
const expectedLaunchFailureProviderMappings = [
    ['dotnet', 'dotnet'],
    ['project', 'dotnet'],
    ['coreclr', 'dotnet'],
    ['clr', 'dotnet'],
    ['node', 'node'],
    ['pwa-node', 'node'],
    ['python', 'python'],
    ['debugpy', 'python'],
    ['java', 'java'],
    ['go', 'go'],
    ['rust', 'rust'],
    ['lldb', 'rust'],
    ['cppdbg', 'rust'],
    ['cppvsdbg', 'rust'],
    ['maui', 'maui'],
    ['azure-functions', 'azureFunctions'],
    ['azurefunctions', 'azureFunctions'],
    ['browser', 'browser'],
    ['pwa-chrome', 'browser'],
    ['pwa-msedge', 'browser'],
    ['firefox', 'browser'],
    ['bun', 'bun'],
    ['other', 'other'],
] as const;
const baseLaunchFailureInput = {
    stage: 'debugSession',
    category: 'unknown',
    controller: 'editor',
    mode: 'other',
    providerKind: 'other',
} as const satisfies LaunchFailureInput;
const baseSanitizedLaunchFailure = {
    ...baseLaunchFailureInput,
    exitCodeBucket: 'none',
} as const satisfies SanitizedLaunchFailure;

suite('editor assistance telemetry', () => {
    test('matches independent pre-refactor bounded launch failure fixtures', () => {
        assert.deepStrictEqual(launchFailureStages, expectedLaunchFailureStages);
        assert.deepStrictEqual(launchFailureCategories, expectedLaunchFailureCategories);
        assert.deepStrictEqual(launchFailureControllers, expectedLaunchFailureControllers);
        assert.deepStrictEqual(launchFailureModes, expectedLaunchFailureModes);
        assert.deepStrictEqual(launchFailureProviderKinds, expectedLaunchFailureProviderKinds);
        assert.deepStrictEqual(
            launchFailureExitCodeBuckets,
            expectedLaunchFailureExitCodeCases.map(([exitCodeBucket]) => exitCodeBucket));
    });

    test('preserves every pre-refactor provider spelling and case normalization', () => {
        for (const [providerKind, expected] of expectedLaunchFailureProviderMappings) {
            assert.strictEqual(
                normalizeLaunchFailure({ ...baseLaunchFailureInput, providerKind }).providerKind,
                expected,
                providerKind);
            assert.strictEqual(
                normalizeLaunchFailure({
                    ...baseLaunchFailureInput,
                    providerKind: providerKind.toUpperCase(),
                }).providerKind,
                expected,
                `${providerKind} uppercase`);
        }
    });

    test('normalizes and projects every independently expected bounded launch failure value', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        let now = 0;
        const telemetry = new EditorAssistanceTelemetry({
            clock: { now: () => now++ },
            sendEvent: (eventName, properties, measurements) => {
                events.push({ eventName, properties, measurements });
            },
        });

        for (const stage of expectedLaunchFailureStages) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, stage },
                { ...baseSanitizedLaunchFailure, stage });
        }
        for (const category of expectedLaunchFailureCategories) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, category },
                { ...baseSanitizedLaunchFailure, category });
        }
        for (const controller of expectedLaunchFailureControllers) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, controller },
                { ...baseSanitizedLaunchFailure, controller });
        }
        for (const mode of expectedLaunchFailureModes) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, mode },
                { ...baseSanitizedLaunchFailure, mode });
        }
        for (const providerKind of expectedLaunchFailureProviderKinds) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, providerKind },
                { ...baseSanitizedLaunchFailure, providerKind });
        }
        for (const [exitCodeBucket, input] of expectedLaunchFailureExitCodeCases) {
            await assertLaunchFailureIsNormalizedAndProjected(
                telemetry,
                events,
                { ...baseLaunchFailureInput, ...input },
                { ...baseSanitizedLaunchFailure, exitCodeBucket });
        }
    });

    test('bounds unknown launch failure values and omits them from telemetry', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        let now = 0;
        const telemetry = new EditorAssistanceTelemetry({
            clock: { now: () => now++ },
            sendEvent: (eventName, properties, measurements) => {
                events.push({ eventName, properties, measurements });
            },
        });
        const unknownValues = [
            'private-stage',
            'private-category',
            'private-controller',
            'private-mode',
            'private-provider',
            'private-exit-code',
            'private-outcome',
        ];
        assert.deepStrictEqual(normalizeLaunchFailure({
            stage: unknownValues[0],
            category: unknownValues[1],
            controller: unknownValues[2],
            mode: unknownValues[3],
            providerKind: unknownValues[4],
            exitCode: unknownValues[5],
        } as unknown as LaunchFailureInput), {
            stage: 'debugSession',
            category: 'unknown',
            controller: 'editor',
            mode: 'other',
            providerKind: 'other',
            exitCodeBucket: 'other',
        });

        await telemetry.capture(aspireExplainLaunchFailureToolName, async () => ({
            success: true,
            tool: aspireExplainLaunchFailureToolName,
            outcome: 'failureFound',
            appHost: 'AppHost/AppHost.csproj',
            stage: unknownValues[0],
            category: unknownValues[1],
            controller: unknownValues[2],
            mode: unknownValues[3],
            providerKind: unknownValues[4],
            exitCodeBucket: unknownValues[5],
            recommendedActions: [],
        } as unknown as EditorAssistanceToolResult));
        await telemetry.capture(aspireExplainLaunchFailureToolName, async () => ({
            success: false,
            tool: aspireExplainLaunchFailureToolName,
            outcome: unknownValues[6],
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(events.slice(-2), [
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireExplainLaunchFailureToolName,
                    outcome: 'failureFound',
                    source: 'languageModelTool',
                },
                measurements: { duration_ms: 1 },
            },
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireExplainLaunchFailureToolName,
                    outcome: 'error',
                    source: 'languageModelTool',
                },
                measurements: { duration_ms: 1 },
            },
        ]);
        assertTelemetryOmits(events.slice(-2), unknownValues);
    });

    test('records bounded status fields without AppHost or resource input', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([100, 137], events);
        const sentinels = [
            '/Users/private/AppHost.csproj',
            'resource-secret',
            'https://dashboard-secret.example',
            'raw-error-secret',
            'session-secret',
            '424242',
            '--credential=secret',
            'PRIVATE_ENV=secret',
            'unsafe_path_key',
        ];
        const result = {
            success: true,
            tool: aspireDebugSessionStatusToolName,
            outcome: 'running',
            scope: 'resource',
            controller: 'editor',
            mode: 'debug',
            appHost: sentinels[0],
            resourceName: sentinels[1],
            unsafe_path_key: sentinels[0],
            dashboardUrl: sentinels[2],
            rawError: sentinels[3],
            sessionId: sentinels[4],
            pid: sentinels[5],
            args: [sentinels[6]],
            env: { PRIVATE_ENV: sentinels[7] },
        } as unknown as EditorAssistanceToolResult;

        assert.strictEqual(
            await telemetry.capture(aspireDebugSessionStatusToolName, async () => result),
            result);
        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/editorassistance/result',
            properties: {
                tool: aspireDebugSessionStatusToolName,
                outcome: 'running',
                source: 'languageModelTool',
                scope: 'resource',
                controller: 'editor',
                mode: 'debug',
                state_bucket: 'running',
            },
            measurements: { duration_ms: 37 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('records only sanitized launch failure dimensions', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([50, 58], events);
        const sentinels = [
            '/private/failure/AppHost.csproj',
            'free-text-recommendation-secret',
            'https://failure-secret.example',
            'raw-stack-secret',
            'credential-secret',
            'unsafe_recommendation_key',
        ];
        const result = {
            success: true,
            tool: aspireExplainLaunchFailureToolName,
            outcome: 'failureFound',
            appHost: sentinels[0],
            stage: 'build',
            category: 'buildFailed',
            controller: 'editor',
            mode: 'debug',
            providerKind: 'dotnet',
            exitCodeBucket: 'one',
            recommendedActions: ['fixBuildErrors'],
            unsafe_recommendation_key: sentinels[1],
            url: sentinels[2],
            error: sentinels[3],
            environment: { TOKEN: sentinels[4] },
        } as unknown as EditorAssistanceToolResult;

        await telemetry.capture(aspireExplainLaunchFailureToolName, async () => result);

        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/editorassistance/result',
            properties: {
                tool: aspireExplainLaunchFailureToolName,
                outcome: 'failureFound',
                source: 'languageModelTool',
                controller: 'editor',
                mode: 'debug',
                stage: 'build',
                category: 'buildFailed',
                provider_kind: 'dotnet',
                exit_code_bucket: 'one',
            },
            measurements: { duration_ms: 8 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('records Dashboard presentation and cancellation without its URL', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([10, 12, 20, 25], events);
        const dashboardUrl = 'https://dashboard-secret.example/?token=secret';

        await telemetry.capture(aspireOpenDashboardToolName, async () => ({
            success: true,
            tool: aspireOpenDashboardToolName,
            outcome: 'opened',
            presentation: 'integratedBrowser',
            dashboardUrl,
        } as unknown as EditorAssistanceToolResult));
        await telemetry.capture(aspireOpenDashboardToolName, async () => ({
            success: false,
            tool: aspireOpenDashboardToolName,
            outcome: 'canceled',
            dashboardUrl,
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(events, [
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireOpenDashboardToolName,
                    outcome: 'opened',
                    source: 'languageModelTool',
                    presentation: 'integratedBrowser',
                },
                measurements: { duration_ms: 2 },
            },
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireOpenDashboardToolName,
                    outcome: 'canceled',
                    source: 'languageModelTool',
                },
                measurements: { duration_ms: 5 },
            },
        ]);
        assertTelemetryOmits(events, [dashboardUrl]);
    });

    test('records invalid input and workspace trust rejection as bounded outcomes', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([30, 31, 40, 42], events);

        await telemetry.capture(aspireDebugSessionStatusToolName, async () => ({
            success: false,
            tool: aspireDebugSessionStatusToolName,
            outcome: 'invalidInput',
        }));
        await telemetry.capture(aspireOpenOutputToolName, async () => ({
            success: false,
            tool: aspireOpenOutputToolName,
            outcome: 'workspaceNotTrusted',
        }));

        assert.deepStrictEqual(events, [
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireDebugSessionStatusToolName,
                    outcome: 'invalidInput',
                    source: 'languageModelTool',
                },
                measurements: { duration_ms: 1 },
            },
            {
                eventName: 'aspire/vscode/editorassistance/result',
                properties: {
                    tool: aspireOpenOutputToolName,
                    outcome: 'workspaceNotTrusted',
                    source: 'languageModelTool',
                },
                measurements: { duration_ms: 2 },
            },
        ]);
    });

    test('records a bounded error when an invocation throws', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([70, 73], events);
        const sentinels = [
            'raw-error-secret',
            '/private/output-secret',
            'https://output-secret.example',
            'caller-extension-id-secret',
        ];
        const error = Object.assign(new Error(sentinels[0]), {
            path: sentinels[1],
            url: sentinels[2],
            extensionId: sentinels[3],
        });

        await assert.rejects(
            telemetry.capture(aspireOpenOutputToolName, async () => { throw error; }),
            error);

        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/editorassistance/result',
            properties: {
                tool: aspireOpenOutputToolName,
                outcome: 'error',
                source: 'languageModelTool',
            },
            measurements: { duration_ms: 3 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('does not serialize list session summaries or aggregate counts', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([90, 94], events);
        const sentinels = [
            '/private/list/AppHost.csproj',
            'session-resource-secret',
            'raw-debug-config-secret',
            '98765',
        ];

        await telemetry.capture(aspireListDebugSessionsToolName, async () => ({
            success: true,
            tool: aspireListDebugSessionsToolName,
            outcome: 'sessionsFound',
            sessions: [{
                appHost: sentinels[0],
                state: 'running',
                controller: 'editor',
                mode: 'debug',
                resources: [sentinels[1]],
                debugConfiguration: sentinels[2],
                pid: sentinels[3],
            }],
            truncated: true,
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/editorassistance/result',
            properties: {
                tool: aspireListDebugSessionsToolName,
                outcome: 'sessionsFound',
                source: 'languageModelTool',
            },
            measurements: { duration_ms: 4 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('records an undecidable AppHost relationship as its own list outcome', async () => {
        // The list refuses whenever one of its AppHosts cannot be related to a running one.
        // Reporting that as a generic failure would make an undecidable relationship
        // indistinguishable from an extension fault in the recorded telemetry.
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([10, 12], events);

        await telemetry.capture(aspireListDebugSessionsToolName, async () => ({
            success: false,
            tool: aspireListDebugSessionsToolName,
            outcome: 'ambiguousAppHost',
            sessions: [],
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(
            events.map(event => event.properties.outcome),
            ['ambiguousAppHost']);
    });
    test('records the Hot Reload controller and outcome without evidence, names, or paths', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([10, 21], events);
        const sentinels = [
            '/private/hotreload/AppHost.csproj',
            'hot-reload-resource-secret',
            'csharp.experimental.debug.hotReload',
        ];

        await telemetry.capture(aspireHotReloadStatusToolName, async () => ({
            success: true,
            tool: aspireHotReloadStatusToolName,
            outcome: 'applicable',
            appHost: sentinels[0],
            resourceName: sentinels[1],
            controller: 'editor',
            hotReloadEnabled: true,
            evidence: ['devKitInstalled', 'hotReloadSettingEnabled', sentinels[2]],
            fallback: ['restartResource', 'rebuildAndRestartAppHost'],
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(events, [{
            eventName: 'aspire/vscode/editorassistance/result',
            properties: {
                tool: aspireHotReloadStatusToolName,
                outcome: 'applicable',
                source: 'languageModelTool',
                controller: 'editor',
            },
            measurements: { duration_ms: 11 },
        }]);
        assertTelemetryOmits(events, sentinels);
    });

    test('records every bounded Hot Reload outcome and drops unknown ones', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const boundedOutcomes = [
            'applicable',
            'notApplicable',
            'noEditorControlledResource',
            'appHostNotRunning',
            'resourceNotFound',
            'resourceAmbiguous',
            'tooManyActiveAppHosts',
            'appHostNotFound',
            'ambiguousAppHost',
            'workspaceNotTrusted',
            'invalidInput',
            'canceled',
            'error',
        ];
        const telemetry = createTelemetry(
            Array.from({ length: (boundedOutcomes.length + 1) * 2 }, () => 0),
            events);

        for (const outcome of [...boundedOutcomes, 'hotReloadApplied']) {
            await telemetry.capture(aspireHotReloadStatusToolName, async () => ({
                success: false,
                tool: aspireHotReloadStatusToolName,
                outcome,
            } as unknown as EditorAssistanceToolResult));
        }

        assert.deepStrictEqual(
            events.map(event => event.properties.outcome),
            // An outcome the tool cannot produce is reported as `error` rather than forwarded,
            // so a future result shape cannot widen this event's value set on its own.
            [...boundedOutcomes, 'error']);
        assertTelemetryOmits(events, ['hotReloadApplied']);
    });

    test('records an externally controlled AppHost as its own controller value', async () => {
        const events: EditorAssistanceTelemetryEvent[] = [];
        const telemetry = createTelemetry([0, 1, 1, 2], events);

        // `external` is a controller the editor-assistance results really produce. Dropping it
        // would silently attribute those invocations to no controller at all.
        await telemetry.capture(aspireDebugSessionStatusToolName, async () => ({
            success: true,
            tool: aspireDebugSessionStatusToolName,
            outcome: 'running',
            scope: 'appHost',
            controller: 'external',
            mode: 'other',
            appHost: 'AppHost/AppHost.csproj',
        } as unknown as EditorAssistanceToolResult));
        await telemetry.capture(aspireHotReloadStatusToolName, async () => ({
            success: true,
            tool: aspireHotReloadStatusToolName,
            outcome: 'notApplicable',
            appHost: 'AppHost/AppHost.csproj',
            resourceName: 'api',
            controller: 'external',
            hotReloadEnabled: true,
            evidence: [],
            fallback: [],
        } as unknown as EditorAssistanceToolResult));

        assert.deepStrictEqual(events.map(event => event.properties.controller), ['external', 'external']);
    });
});

function createTelemetry(
    times: readonly number[],
    events: EditorAssistanceTelemetryEvent[]): EditorAssistanceTelemetry {
    let index = 0;
    return new EditorAssistanceTelemetry({
        clock: { now: () => times[index++] },
        sendEvent: (eventName, properties, measurements) => {
            events.push({ eventName, properties, measurements });
        },
    });
}

async function assertLaunchFailureIsNormalizedAndProjected(
    telemetry: EditorAssistanceTelemetry,
    events: EditorAssistanceTelemetryEvent[],
    input: LaunchFailureInput,
    expectedFailure: SanitizedLaunchFailure): Promise<void> {
    const failure = normalizeLaunchFailure(input);
    assert.deepStrictEqual(failure, expectedFailure);
    const previousEventCount = events.length;

    await telemetry.capture(aspireExplainLaunchFailureToolName, async () => ({
        success: true,
        tool: aspireExplainLaunchFailureToolName,
        outcome: 'failureFound',
        appHost: 'AppHost/AppHost.csproj',
        ...failure,
        recommendedActions: [],
    }));

    assert.strictEqual(events.length, previousEventCount + 1);
    assert.deepStrictEqual(events.at(-1), {
        eventName: 'aspire/vscode/editorassistance/result',
        properties: {
            tool: aspireExplainLaunchFailureToolName,
            outcome: 'failureFound',
            source: 'languageModelTool',
            controller: expectedFailure.controller,
            mode: expectedFailure.mode,
            stage: expectedFailure.stage,
            category: expectedFailure.category,
            provider_kind: expectedFailure.providerKind,
            exit_code_bucket: expectedFailure.exitCodeBucket,
        },
        measurements: { duration_ms: 1 },
    });
}

function assertTelemetryOmits(events: readonly EditorAssistanceTelemetryEvent[], sentinels: readonly string[]): void {
    const serialized = JSON.stringify(events);
    for (const sentinel of sentinels) {
        assert.strictEqual(
            serialized.includes(sentinel),
            false,
            `Telemetry contained unsafe sentinel '${sentinel}'. Payload: ${serialized}`);
    }
}
