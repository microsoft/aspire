import {
    type ResourceAttachProviderId,
    type ResourceDebugErrorKind,
    type ResourceDebugResult,
    type ResourceDebugSource,
    type ResourceDebugStrategy,
} from './resourceDebugContracts';
import { sendTelemetryEvent } from '../utils/telemetry';

export type ResourceDebugResourceType = 'project' | 'executable' | 'container' | 'other';
export type ResourceDebugResourceState = 'running' | 'notRunning' | 'unknown';
export type ResourceDebugDebuggerRequirement = 'installed' | 'missing' | 'none';
export type ResourceDebugRequestedStrategyTelemetryBucket = ResourceDebugStrategy | 'invalid';

export interface ResourceDebugClock {
    now(): number;
}

export interface ResourceDebugStartTelemetryProperties {
    readonly source: ResourceDebugSource;
    readonly requested_strategy: ResourceDebugRequestedStrategyTelemetryBucket;
    readonly controller: 'editor';
}

export interface ResourceDebugResultTelemetryProperties {
    readonly source: ResourceDebugSource;
    readonly provider: ResourceAttachProviderId | 'none';
    readonly resource_type?: ResourceDebugResourceType;
    readonly requested_strategy: ResourceDebugRequestedStrategyTelemetryBucket;
    readonly effective_strategy: 'attach' | 'none';
    readonly outcome: ResourceDebugResult['outcome'];
    readonly controller: 'editor';
    readonly state: ResourceDebugResourceState;
    readonly debugger_requirement: ResourceDebugDebuggerRequirement;
    readonly error_kind: ResourceDebugErrorKind | 'none';
}

export interface ResourceDebugResultTelemetryMeasurements {
    readonly resolution_duration_ms?: number;
    readonly debug_start_duration_ms?: number;
    readonly total_duration_ms?: number;
}

export interface ResourceDebugAttachSessionMetadata {
    readonly source: ResourceDebugSource;
    readonly provider: ResourceAttachProviderId;
    readonly resource_type: ResourceDebugResourceType;
    readonly requested_strategy: ResourceDebugStrategy;
    readonly effective_strategy: 'attach';
}

export interface ResourceDebugSessionEndTelemetryProperties extends ResourceDebugAttachSessionMetadata {
    readonly controller: 'editor';
    readonly session_end_reason: 'terminated';
}

export interface ResourceDebugSessionEndTelemetryMeasurements {
    readonly session_duration_ms?: number;
}

export interface ResourceDebugTelemetry {
    recordStart(properties: ResourceDebugStartTelemetryProperties): void;
    recordResult(
        properties: ResourceDebugResultTelemetryProperties,
        measurements: ResourceDebugResultTelemetryMeasurements,
    ): void;
    recordSessionEnd(
        properties: ResourceDebugSessionEndTelemetryProperties,
        measurements: ResourceDebugSessionEndTelemetryMeasurements,
    ): void;
}

export const monotonicResourceDebugClock: ResourceDebugClock = {
    now: () => performance.now(),
};

/**
 * Sends only the resource-debug telemetry schema. Keeping the event shapes here means the
 * service and session registry cannot accidentally forward debug configurations or errors.
 */
export class ExtensionResourceDebugTelemetry implements ResourceDebugTelemetry {
    recordStart(properties: ResourceDebugStartTelemetryProperties): void {
        this._send(() => sendTelemetryEvent('aspire/vscode/resourcedebug/start', properties));
    }

    recordResult(
        properties: ResourceDebugResultTelemetryProperties,
        measurements: ResourceDebugResultTelemetryMeasurements,
    ): void {
        this._send(() => sendTelemetryEvent('aspire/vscode/resourcedebug/result', properties, measurements));
    }

    recordSessionEnd(
        properties: ResourceDebugSessionEndTelemetryProperties,
        measurements: ResourceDebugSessionEndTelemetryMeasurements,
    ): void {
        this._send(() => sendTelemetryEvent('aspire/vscode/resourcedebug/session/end', properties, measurements));
    }

    private _send(send: () => void): void {
        try {
            send();
        }
        catch {
            // Telemetry is observational. A transport failure must not change resource debugging.
        }
    }
}
