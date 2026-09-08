import { getOrCreateIdentityForCurrentAppHostTarget, type OpaqueAppHostIdentity } from '../utils/appHostIdentity';
import { classifyAppHostPath } from '../utils/appHostLanguage';
import {
    isCommandCancellation,
    sendTelemetryEvent,
    type EventMeasurements,
    type EventProperties,
} from '../utils/telemetry';

export const launchFailureStages = Object.freeze([
    'discovery',
    'validation',
    'cliLaunch',
    'build',
    'dcpStartup',
    'debugSession',
    'dashboard',
] as const);
export type LaunchFailureStage = typeof launchFailureStages[number];

export const launchFailureCategories = Object.freeze([
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
] as const);
export type LaunchFailureCategory = typeof launchFailureCategories[number];

export const launchFailureControllers = Object.freeze(['editor', 'cli'] as const);
export type LaunchFailureController = typeof launchFailureControllers[number];

export const launchFailureModes = Object.freeze(['run', 'debug', 'deploy', 'publish', 'other'] as const);
export type LaunchFailureMode = typeof launchFailureModes[number];

export const launchFailureProviderKinds = Object.freeze([
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
] as const);
export type LaunchFailureProviderKind = typeof launchFailureProviderKinds[number];

export function getLaunchFailureProviderKindForAppHostPath(appHostPath: string | undefined): LaunchFailureProviderKind {
    switch (classifyAppHostPath(appHostPath)) {
        case 'csharp':
            return 'dotnet';
        case 'typescript':
            return 'node';
        case 'java':
            return 'java';
        case 'rust':
            return 'rust';
        default:
            return 'other';
    }
}

export const launchFailureExitCodeBuckets = Object.freeze(['none', 'zero', 'one', 'signal', 'other'] as const);
export type LaunchFailureExitCodeBucket = typeof launchFailureExitCodeBuckets[number];

export interface SanitizedLaunchFailure {
    readonly stage: LaunchFailureStage;
    readonly category: LaunchFailureCategory;
    readonly controller: LaunchFailureController;
    readonly mode: LaunchFailureMode;
    readonly providerKind: LaunchFailureProviderKind;
    readonly exitCodeBucket: LaunchFailureExitCodeBucket;
}

export interface LaunchFailureRecord extends SanitizedLaunchFailure {
    readonly appHostIdentity: OpaqueAppHostIdentity;
    readonly recordedAt: number;
    readonly sequence: number;
}

export interface LaunchFailureInput {
    readonly stage: LaunchFailureStage;
    readonly category?: LaunchFailureCategory | unknown;
    readonly controller: LaunchFailureController;
    readonly mode?: LaunchFailureMode | unknown;
    readonly providerKind?: LaunchFailureProviderKind | string | unknown;
    readonly exitCode?: number | null | unknown;
    readonly signal?: string | null | unknown;
    readonly error?: unknown;
    /**
     * Set only when the boundary owns the timeout. A CancellationError without this
     * marker remains a cancellation rather than being guessed to be a timeout.
     */
    readonly timedOut?: boolean;
}

export interface LaunchFailureJournalClock {
    now(): number;
}

const launchFailureRecordedEventName = 'aspire/vscode/launchfailure/recorded' as const;
type LaunchFailureRecordedProperties = EventProperties<typeof launchFailureRecordedEventName>;
type LaunchFailureRecordedMeasurements = EventMeasurements<typeof launchFailureRecordedEventName>;

export interface LaunchFailureRecordedTelemetryEvent {
    readonly eventName: typeof launchFailureRecordedEventName;
    readonly properties: LaunchFailureRecordedProperties;
    readonly measurements: LaunchFailureRecordedMeasurements;
}

export type LaunchFailureRecordAccepted = (
    failure: SanitizedLaunchFailure,
    journalSize: number) => void;

const ignoreAcceptedLaunchFailure: LaunchFailureRecordAccepted = () => undefined;
const launchFailureTtlMs = 30 * 60 * 1_000;
const maxFailuresPerAppHost = 5;
const maxFailuresGlobally = 50;
const opaqueAppHostIdentityPattern = /^apphost-[1-9]\d*$/;

const stages = new Set<LaunchFailureStage>(launchFailureStages);
const categories = new Set<LaunchFailureCategory>(launchFailureCategories);
const controllers = new Set<LaunchFailureController>(launchFailureControllers);
const modes = new Set<LaunchFailureMode>(launchFailureModes);
const exitCodeBuckets = new Set<LaunchFailureExitCodeBucket>(launchFailureExitCodeBuckets);

const providerKindAliases: readonly (readonly [string, LaunchFailureProviderKind])[] = [
    ['project', 'dotnet'],
    ['coreclr', 'dotnet'],
    ['clr', 'dotnet'],
    ['pwa-node', 'node'],
    ['debugpy', 'python'],
    ['lldb', 'rust'],
    ['cppdbg', 'rust'],
    ['cppvsdbg', 'rust'],
    ['azure-functions', 'azureFunctions'],
    ['pwa-chrome', 'browser'],
    ['pwa-msedge', 'browser'],
    ['firefox', 'browser'],
];
const providerKindByAlias = new Map<string, LaunchFailureProviderKind>([
    ...launchFailureProviderKinds.map(providerKind =>
        [providerKind.toLowerCase(), providerKind] as const),
    ...providerKindAliases,
]);

/**
 * Projects transient failure context into a newly allocated, finite record.
 *
 * Raw errors are inspected only for stable names/codes and cancellation identity. No
 * message, stack, output, path, URL, arguments, environment, or configuration object is
 * copied into the returned value.
 */
export function normalizeLaunchFailure(input: LaunchFailureInput): SanitizedLaunchFailure {
    return {
        stage: stages.has(input.stage) ? input.stage : 'debugSession',
        category: normalizeCategory(input),
        controller: controllers.has(input.controller) ? input.controller : 'editor',
        mode: normalizeMode(input.mode),
        providerKind: normalizeProviderKind(input.providerKind),
        exitCodeBucket: normalizeExitCodeBucket(input.exitCode, input.signal),
    };
}

export class LaunchFailureJournal {
    private readonly _records: LaunchFailureRecord[] = [];
    private _nextSequence = 0;

    constructor(
        private readonly _clock: LaunchFailureJournalClock = { now: Date.now },
        private readonly _onRecordAccepted: LaunchFailureRecordAccepted = ignoreAcceptedLaunchFailure) {
    }

    record(appHostIdentity: OpaqueAppHostIdentity, failure: SanitizedLaunchFailure): LaunchFailureRecord {
        if (!opaqueAppHostIdentityPattern.test(appHostIdentity)) {
            throw new TypeError('Launch failure journal requires an opaque AppHost identity.');
        }

        this.pruneExpired();
        const record: LaunchFailureRecord = {
            appHostIdentity,
            stage: stages.has(failure.stage) ? failure.stage : 'debugSession',
            category: categories.has(failure.category) ? failure.category : 'unknown',
            controller: controllers.has(failure.controller) ? failure.controller : 'editor',
            mode: normalizeMode(failure.mode),
            providerKind: normalizeProviderKind(failure.providerKind),
            exitCodeBucket: exitCodeBuckets.has(failure.exitCodeBucket) ? failure.exitCodeBucket : 'none',
            recordedAt: this._clock.now(),
            sequence: ++this._nextSequence,
        };
        this._records.push(record);

        const appHostRecords = this._records.filter(candidate => candidate.appHostIdentity === appHostIdentity);
        if (appHostRecords.length > maxFailuresPerAppHost) {
            const oldest = appHostRecords[0];
            this._records.splice(this._records.indexOf(oldest), 1);
        }

        while (this._records.length > maxFailuresGlobally) {
            this._records.shift();
        }

        this._onRecordAccepted(toSanitizedLaunchFailure(record), this._records.length);
        return { ...record };
    }

    readLatest(appHostIdentity?: OpaqueAppHostIdentity): readonly LaunchFailureRecord[] {
        this.pruneExpired();
        const records = appHostIdentity
            ? this._records.filter(record => record.appHostIdentity === appHostIdentity)
            : this._records;

        return records.slice().reverse().map(record => ({ ...record }));
    }

    clear(): void {
        this._records.splice(0);
        this._nextSequence = 0;
    }

    private pruneExpired(): void {
        const oldestAllowed = this._clock.now() - launchFailureTtlMs;
        let expired = 0;
        while (expired < this._records.length && this._records[expired].recordedAt <= oldestAllowed) {
            expired++;
        }

        if (expired > 0) {
            this._records.splice(0, expired);
        }
    }
}

/**
 * Emits the finite launch-failure projection accepted by the in-memory journal.
 *
 * The journal calls this only after TTL and capacity maintenance. AppHost identity,
 * timestamps, sequence numbers, and all raw capture input stay outside the callback.
 */
export function sendLaunchFailureRecordedTelemetry(
    failure: SanitizedLaunchFailure,
    journalSize: number,
    sendEvent: (
        eventName: typeof launchFailureRecordedEventName,
        properties: LaunchFailureRecordedProperties,
        measurements: LaunchFailureRecordedMeasurements) => void = sendTelemetryEvent): void {
    sendEvent(
        launchFailureRecordedEventName,
        {
            stage: stages.has(failure.stage) ? failure.stage : 'debugSession',
            category: categories.has(failure.category) ? failure.category : 'unknown',
            controller: controllers.has(failure.controller) ? failure.controller : 'editor',
            mode: normalizeMode(failure.mode),
            provider_kind: normalizeProviderKind(failure.providerKind),
            exit_code_bucket: exitCodeBuckets.has(failure.exitCodeBucket) ? failure.exitCodeBucket : 'none',
        },
        {
            journal_size: Number.isFinite(journalSize)
                ? Math.min(maxFailuresGlobally, Math.max(0, Math.floor(journalSize)))
                : 0,
        });
}

const defaultLaunchFailureJournal = new LaunchFailureJournal(
    { now: Date.now },
    sendLaunchFailureRecordedTelemetry);

export function recordLaunchFailureForAppHostPath(appHostPath: string, input: LaunchFailureInput): LaunchFailureRecord {
    return recordLaunchFailureForAppHostIdentity(
        getOrCreateIdentityForCurrentAppHostTarget(appHostPath),
        input);
}

export function recordLaunchFailureForAppHostIdentity(appHostIdentity: OpaqueAppHostIdentity, input: LaunchFailureInput): LaunchFailureRecord {
    return defaultLaunchFailureJournal.record(appHostIdentity, normalizeLaunchFailure(input));
}

export function recordSanitizedLaunchFailureForAppHostPath(appHostPath: string, failure: SanitizedLaunchFailure): LaunchFailureRecord {
    return recordSanitizedLaunchFailureForAppHostIdentity(
        getOrCreateIdentityForCurrentAppHostTarget(appHostPath),
        failure);
}

export function recordSanitizedLaunchFailureForAppHostIdentity(appHostIdentity: OpaqueAppHostIdentity, failure: SanitizedLaunchFailure): LaunchFailureRecord {
    return defaultLaunchFailureJournal.record(appHostIdentity, failure);
}

export function readLatestLaunchFailures(appHostPath?: string): readonly LaunchFailureRecord[] {
    const identity = appHostPath ? getOrCreateIdentityForCurrentAppHostTarget(appHostPath) : undefined;
    return defaultLaunchFailureJournal.readLatest(identity);
}

export function resetLaunchFailureJournal(): void {
    defaultLaunchFailureJournal.clear();
}

export function __resetLaunchFailureJournalForTests(): void {
    resetLaunchFailureJournal();
}

function toSanitizedLaunchFailure(record: LaunchFailureRecord): SanitizedLaunchFailure {
    return {
        stage: record.stage,
        category: record.category,
        controller: record.controller,
        mode: record.mode,
        providerKind: record.providerKind,
        exitCodeBucket: record.exitCodeBucket,
    };
}

function normalizeCategory(input: LaunchFailureInput): LaunchFailureCategory {
    if (categories.has(input.category as LaunchFailureCategory)) {
        return input.category as LaunchFailureCategory;
    }

    if (input.timedOut === true) {
        return 'timeout';
    }

    if (isCommandCancellation(input.error)) {
        return 'canceled';
    }

    const identifiers = getErrorIdentifiers(input.error);
    if (identifiers.some(identifier => identifier === 'EADDRINUSE')) {
        return 'portConflict';
    }
    if (identifiers.some(identifier => identifier === 'EACCES' || identifier === 'EPERM')) {
        return 'permissionDenied';
    }
    if (identifiers.some(identifier =>
        identifier === 'ENOENT' ||
        identifier === 'MODULE_NOT_FOUND' ||
        identifier === 'ERR_MODULE_NOT_FOUND')) {
        return 'missingDependency';
    }
    if (identifiers.some(identifier =>
        identifier === 'UnsupportedError' ||
        identifier === 'NotSupportedError' ||
        identifier === 'ERR_UNSUPPORTED_OPERATION')) {
        return 'unsupported';
    }

    return 'unknown';
}

function normalizeMode(mode: LaunchFailureInput['mode']): LaunchFailureMode {
    return modes.has(mode as LaunchFailureMode) ? mode as LaunchFailureMode : 'other';
}

function normalizeProviderKind(providerKind: LaunchFailureInput['providerKind']): LaunchFailureProviderKind {
    return typeof providerKind === 'string'
        ? providerKindByAlias.get(providerKind.toLowerCase()) ?? 'other'
        : 'other';
}

function normalizeExitCodeBucket(exitCode: LaunchFailureInput['exitCode'], signal: LaunchFailureInput['signal']): LaunchFailureExitCodeBucket {
    if (typeof signal === 'string' && signal.length > 0) {
        return 'signal';
    }
    if (exitCode === undefined || exitCode === null) {
        return 'none';
    }
    if (exitCode === 0) {
        return 'zero';
    }
    if (exitCode === 1) {
        return 'one';
    }

    return 'other';
}

function getErrorIdentifiers(error: unknown): readonly string[] {
    if (!error || typeof error !== 'object') {
        return [];
    }

    const candidate = error as { code?: unknown; name?: unknown };
    const identifiers: string[] = [];
    if (typeof candidate.code === 'string') {
        identifiers.push(candidate.code);
    }
    if (typeof candidate.name === 'string') {
        identifiers.push(candidate.name);
    }

    return identifiers;
}
