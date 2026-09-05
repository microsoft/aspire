import {
    aspireDebugSessionStatusToolName,
    aspireExplainLaunchFailureToolName,
    aspireHotReloadStatusToolName,
    aspireOpenDashboardToolName,
    type EditorAssistanceToolName,
    type EditorAssistanceToolResult,
} from './editorAssistanceToolContracts';
import {
    sendTelemetryEvent,
    type EventMeasurements,
    type EventProperties,
} from '../utils/telemetry';
import {
    launchFailureCategories,
    launchFailureControllers,
    launchFailureExitCodeBuckets,
    launchFailureModes,
    launchFailureProviderKinds,
    launchFailureStages,
} from '../services/launchFailureJournal';

const editorAssistanceResultEventName = 'aspire/vscode/editorassistance/result' as const;

type ResultEventProperties = EventProperties<typeof editorAssistanceResultEventName>;
type ResultEventMeasurements = EventMeasurements<typeof editorAssistanceResultEventName>;

export interface EditorAssistanceTelemetryClock {
    now(): number;
}

export interface EditorAssistanceTelemetryEvent {
    readonly eventName: typeof editorAssistanceResultEventName;
    readonly properties: ResultEventProperties;
    readonly measurements: ResultEventMeasurements;
}

export interface EditorAssistanceTelemetryOptions {
    readonly clock?: EditorAssistanceTelemetryClock;
    readonly sendEvent?: (
        eventName: typeof editorAssistanceResultEventName,
        properties: ResultEventProperties,
        measurements: ResultEventMeasurements) => void;
}

const outcomesByTool: Readonly<Record<EditorAssistanceToolName, ReadonlySet<string>>> = {
    aspire_debug_session_status: new Set([
        'running',
        'starting',
        'stopping',
        'notDebugging',
        'multipleSessions',
        'appHostNotFound',
        'ambiguousAppHost',
        'resourceNotFound',
        'resourceAmbiguous',
        'workspaceNotTrusted',
        'invalidInput',
        'canceled',
        'error',
    ]),
    aspire_explain_launch_failure: new Set([
        'failureFound',
        'noRecordedFailure',
        'appHostNotFound',
        'ambiguousAppHost',
        'workspaceNotTrusted',
        'invalidInput',
        'canceled',
        'error',
    ]),
    aspire_open_dashboard: new Set([
        'opened',
        'dashboardUnavailable',
        'appHostNotRunning',
        'appHostNotFound',
        'ambiguousAppHost',
        'workspaceNotTrusted',
        'invalidInput',
        'canceled',
        'error',
    ]),
    aspire_open_output: new Set([
        'opened',
        'workspaceNotTrusted',
        'invalidInput',
        'canceled',
        'error',
    ]),
    aspire_list_debug_sessions: new Set([
        'sessionsFound',
        'noSessions',
        'ambiguousAppHost',
        'workspaceNotTrusted',
        'invalidInput',
        'canceled',
        'error',
    ]),
    aspire_hot_reload_status: new Set([
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
    ]),
};

const statusStateBuckets = new Set([
    'running',
    'starting',
    'stopping',
    'notDebugging',
    'multipleSessions',
]);
const scopes = new Set(['appHost', 'resource']);
// Editor-assistance results describe who controls an AppHost (`editor`/`external`), while a
// recorded launch failure describes who launched it (`editor`/`cli`). Both reach this one
// `controller` property, so the allowed set is their union; narrowing it to either side alone
// would silently drop the property for the other and attribute those invocations to nothing.
const controllers = new Set<string>([...launchFailureControllers, 'editor', 'external']);
const modes = new Set(launchFailureModes);
const launchFailureStageSet = new Set(launchFailureStages);
const launchFailureCategorySet = new Set(launchFailureCategories);
const providerKinds = new Set(launchFailureProviderKinds);
const exitCodeBuckets = new Set(launchFailureExitCodeBuckets);
const dashboardPresentations = new Set([
    'integratedBrowser',
    'externalBrowser',
    'debugBrowser',
    'notification',
]);

/**
 * Records one finite telemetry event around each language model tool invocation.
 *
 * Tool results can contain safe user-facing display values, such as a workspace-relative
 * AppHost path or resource name. The telemetry projection deliberately rebuilds its payload
 * from bounded enums and never copies those result objects or caller input.
 */
export class EditorAssistanceTelemetry {
    private readonly _clock: EditorAssistanceTelemetryClock;
    private readonly _sendEvent: NonNullable<EditorAssistanceTelemetryOptions['sendEvent']>;

    constructor(options: EditorAssistanceTelemetryOptions = {}) {
        this._clock = options.clock ?? { now: Date.now };
        this._sendEvent = options.sendEvent ?? sendTelemetryEvent;
    }

    async capture<T extends EditorAssistanceToolResult>(
        tool: EditorAssistanceToolName,
        invoke: () => Promise<T>): Promise<T> {
        const startedAt = this._clock.now();
        try {
            const result = await invoke();
            this.record(tool, result, this.getDuration(startedAt));
            return result;
        }
        catch (error) {
            this.record(tool, undefined, this.getDuration(startedAt));
            throw error;
        }
    }

    private record(
        tool: EditorAssistanceToolName,
        result: EditorAssistanceToolResult | undefined,
        durationMs: number): void {
        const outcome = getBoundedOutcome(tool, result?.outcome);
        const properties: ResultEventProperties = {
            tool,
            outcome,
            source: 'languageModelTool',
        };

        if (tool === aspireDebugSessionStatusToolName && result) {
            copyIfBounded(properties, 'scope', result, 'scope', scopes);
            copyIfBounded(properties, 'controller', result, 'controller', controllers);
            copyIfBounded(properties, 'mode', result, 'mode', modes);
            if (statusStateBuckets.has(outcome)) {
                properties.state_bucket = outcome;
            }
        }
        else if (tool === aspireExplainLaunchFailureToolName && result?.outcome === 'failureFound') {
            copyIfBounded(properties, 'controller', result, 'controller', controllers);
            copyIfBounded(properties, 'mode', result, 'mode', modes);
            copyIfBounded(properties, 'stage', result, 'stage', launchFailureStageSet);
            copyIfBounded(properties, 'category', result, 'category', launchFailureCategorySet);
            copyIfBounded(properties, 'provider_kind', result, 'providerKind', providerKinds);
            copyIfBounded(properties, 'exit_code_bucket', result, 'exitCodeBucket', exitCodeBuckets);
        }
        else if (tool === aspireOpenDashboardToolName && result?.outcome === 'opened') {
            copyIfBounded(properties, 'presentation', result, 'presentation', dashboardPresentations);
        }
        else if (tool === aspireHotReloadStatusToolName && result?.success) {
            // Only who controls the AppHost is recorded. The evidence identifiers, the enabled
            // state, and the fallback are all derivable from the reported outcome plus this
            // window's own configuration, so recording them would add cardinality without
            // adding a question this event can answer.
            copyIfBounded(properties, 'controller', result, 'controller', controllers);
        }

        this._sendEvent(
            editorAssistanceResultEventName,
            properties,
            { duration_ms: durationMs });
    }

    private getDuration(startedAt: number): number {
        const duration = this._clock.now() - startedAt;
        return Number.isFinite(duration) ? Math.max(0, duration) : 0;
    }
}

function getBoundedOutcome(tool: EditorAssistanceToolName, outcome: unknown): string {
    return typeof outcome === 'string' && outcomesByTool[tool].has(outcome)
        ? outcome
        : 'error';
}

function copyIfBounded(
    target: ResultEventProperties,
    targetProperty: keyof ResultEventProperties,
    source: object,
    sourceProperty: string,
    allowedValues: ReadonlySet<string>): void {
    const value = (source as Record<string, unknown>)[sourceProperty];
    if (typeof value === 'string' && allowedValues.has(value)) {
        target[targetProperty] = value;
    }
}
