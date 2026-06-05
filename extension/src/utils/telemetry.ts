import { TelemetryReporter } from '@vscode/extension-telemetry';
import * as vscode from 'vscode';
import {
    CommonTelemetryProperties,
    CommonTelemetryProperty,
    EventMeasurements,
    EventProperties,
    KnownTelemetryEventName,
} from './telemetryRegistry';

export type {
    KnownTelemetryEventName,
    EventProperties,
    EventMeasurements,
    CommonTelemetryProperty,
    CommonTelemetryProperties,
} from './telemetryRegistry';

// Module-private state.
// Aspire emits all telemetry through a single TelemetryReporter (which itself
// honors `vscode.env.isTelemetryEnabled`, including transitions between
// "on" / "errorsOnly" / "off"). We keep it as a module singleton because the
// reporter is created at activation time and consumed from multiple places —
// the command wrapper, the engagement reporter, the tree view, the debug
// session, and the dashboard telemetry passthrough server.
let reporter: TelemetryReporter | undefined;
const defaultTelemetryReporterFactory = (aiKey: string): TelemetryReporter => new TelemetryReporter(aiKey);
let telemetryReporterFactory = defaultTelemetryReporterFactory;

// Common properties merged into every event we emit. The TelemetryReporter
// already injects extension version, OS, machine id, etc., so this map is
// reserved for Aspire-specific cross-event signals (e.g. detected AppHost
// language, run mode). The key set is intentionally tiny and registered in
// `telemetryRegistry.ts` because each common property duplicates into a row
// per event in the classification catalog.
// Values are kept as strings because @vscode/extension-telemetry only supports
// string-valued properties; numeric data must go through `measurements`.
const commonProperties: Partial<Record<CommonTelemetryProperty, string>> = {};

// Optional listener invoked from {@link withCommandTelemetry} on every
// successful or attempted command invocation. The engagement reporter sets
// this from `meaningfulEngagement.ts` so it can fire its activation event on
// the first command without needing to be plumbed through every callsite.
// Kept as a single optional callback to avoid circular module dependencies
// (telemetry.ts must not import meaningfulEngagement.ts).
let commandInvocationListener: (() => void) | undefined;

export function initializeTelemetry(context: vscode.ExtensionContext): void {
    if (reporter) {
        return;
    }
    // Use the ExtensionContext-provided package metadata so activation and
    // telemetry initialization read from the same extension manifest.
    const aiKey = context.extension.packageJSON.aiKey;
    if (aiKey) {
        reporter = telemetryReporterFactory(aiKey);
        context.subscriptions.push({ dispose: () => reporter?.dispose() });
    }
}

/**
 * Whether telemetry is allowed to leave the machine right now. Combines our
 * reporter availability with VS Code's global telemetry user setting so that
 * the dashboard passthrough endpoint advertises "enabled" only when both are
 * true. The reporter itself enforces the user setting on send, but we also
 * gate the dashboard's session-start handshake to avoid pointless traffic.
 */
export function isExtensionTelemetryEnabled(): boolean {
    return reporter !== undefined && vscode.env.isTelemetryEnabled;
}

/**
 * Sets one or more common properties that will be merged into every event
 * emitted via {@link sendTelemetryEvent}, {@link sendTelemetryErrorEvent}, and
 * {@link withCommandTelemetry}. Existing values for the same keys are replaced.
 * `undefined` values clear a key.
 *
 * The key set is restricted to {@link CommonTelemetryProperty} on purpose:
 * every common property creates a (event, property) row in the classification
 * catalog for *every* event we emit, so adding one is a deliberate decision
 * that must go through `telemetryRegistry.ts`.
 */
export function setCommonTelemetryProperties(properties: CommonTelemetryProperties): void {
    for (const [key, value] of Object.entries(properties) as Array<[CommonTelemetryProperty, string | undefined]>) {
        if (value === undefined) {
            delete commonProperties[key];
        }
        else {
            commonProperties[key] = value;
        }
    }
}

export function getCommonTelemetryProperties(): Readonly<Partial<Record<CommonTelemetryProperty, string>>> {
    return commonProperties;
}

function mergeProperties<E extends KnownTelemetryEventName>(properties?: EventProperties<E>): { [key: string]: string } {
    // Spread order matters: explicit per-event properties win over commons so
    // a caller can override (e.g. tests forcing apphost_present to a known
    // value). The result is intentionally widened to `{ [key: string]: string }`
    // because that's what the underlying TelemetryReporter expects — the
    // narrow typing is enforced at the public wrapper boundary above.
    return { ...commonProperties, ...((properties ?? {}) as { [key: string]: string }) };
}

/**
 * Emit a telemetry event. The `eventName` is constrained to entries in
 * {@link KnownTelemetryEventName} (see telemetryRegistry.ts) and the
 * accepted `properties` / `measurements` keys are constrained to the per-event
 * union declared there. This prevents accidental introduction of new
 * (event, property) pairs that would need data classification.
 */
export function sendTelemetryEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    reporter?.sendTelemetryEvent(eventName, mergeProperties(properties), measurements as { [key: string]: number } | undefined);
}

/**
 * Emits an error telemetry event. Use for faults (unexpected exceptions,
 * dashboard fault posts, etc.) — the underlying reporter applies stricter
 * PII scrubbing on error events than on regular events.
 */
export function sendTelemetryErrorEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    reporter?.sendTelemetryErrorEvent(eventName, mergeProperties(properties), measurements as { [key: string]: number } | undefined);
}

/**
 * Outcome bucket reported for every command invocation.
 *  - `success`     : the command's promise resolved normally.
 *  - `canceled`    : the user dismissed a quick pick / input box, or the
 *                    command threw `vscode.CancellationError`. We treat this
 *                    distinctly from errors so dashboards aren't polluted by
 *                    routine user "back out" actions.
 *  - `error`       : the command threw or rejected with anything else.
 */
export type CommandOutcome = 'success' | 'canceled' | 'error';

export interface CommandInvocationEvent {
    command: string;
    outcome: CommandOutcome;
    durationMs: number;
    source?: string;
    errorKind?: string;
}

const commandInvocationEmitter = new vscode.EventEmitter<CommandInvocationEvent>();
export const onDidInvokeCommand = commandInvocationEmitter.event;

/**
 * Wraps an extension command invocation so we capture invocation, outcome and
 * duration in one place. Every `vscode.commands.registerCommand` callback in
 * the extension should be routed through here so we get consistent telemetry
 * shape across the surface (command palette, tree view context menus, code
 * lens links, walkthroughs, etc.).
 *
 * The wrapper does NOT swallow errors — exceptions propagate to the caller so
 * existing error-handling (e.g. `tryExecuteCommand`'s catch block) keeps
 * working. We just observe.
 *
 * @param commandName Fully-qualified command name (e.g. `aspire-vscode.add`).
 * @param fn The command implementation.
 * @param additionalProperties Properties to merge into the emitted event
 *        (after common properties, before outcome/duration). Useful for
 *        per-call dimensions like `source: 'tree'` on tree-view commands.
 */
export async function withCommandTelemetry<T>(
    commandName: string,
    fn: () => Promise<T> | T,
    additionalProperties?: Partial<Record<'source', string>>
): Promise<T> {
    commandInvocationListener?.();
    const startTime = Date.now();
    let outcome: CommandOutcome = 'success';
    let errorKind: string | undefined;
    try {
        return await Promise.resolve(fn());
    }
    catch (err) {
        if (isCancellation(err)) {
            outcome = 'canceled';
        }
        else {
            outcome = 'error';
            errorKind = classifyError(err);
        }
        throw err;
    }
    finally {
        const durationMs = Date.now() - startTime;
        const properties: EventProperties<'command/invoked'> = {
            command: commandName,
            outcome,
            ...(additionalProperties ?? {}),
        };
        if (errorKind) {
            properties.error_kind = errorKind;
        }
        sendTelemetryEvent('command/invoked', properties, { duration_ms: durationMs });
        commandInvocationEmitter.fire({
            command: commandName,
            outcome,
            durationMs,
            source: additionalProperties?.source,
            errorKind,
        });
    }
}

function isCancellation(err: unknown): boolean {
    // VS Code's CancellationError doesn't always reach us by reference (the
    // value can be re-thrown across module boundaries or originate from a
    // QuickPick that the user dismissed silently). Match on the well-known
    // shape used across the extension API instead.
    if (err instanceof Error) {
        if (err.name === 'Canceled' || err.name === 'CancellationError') {
            return true;
        }
        if (typeof err.message === 'string' && err.message.toLowerCase() === 'canceled') {
            return true;
        }
    }
    // QuickPick dismissals occasionally surface as the literal string 'Canceled'.
    return typeof err === 'string' && err.toLowerCase() === 'canceled';
}

function classifyError(err: unknown): string {
    if (err instanceof Error) {
        return err.name || 'Error';
    }
    if (typeof err === 'string') {
        return 'String';
    }
    return typeof err;
}

/**
 * Returns whether the given value looks like a user-driven cancellation. Used
 * by both {@link withCommandTelemetry} and callers that want to bypass
 * user-visible error reporting on cancellation.
 */
export function isCommandCancellation(err: unknown): boolean {
    return isCancellation(err);
}

/**
 * Registers a callback invoked once per {@link withCommandTelemetry} call,
 * regardless of outcome. Designed for the engagement reporter to observe
 * "user did something with the extension" signals without coupling telemetry.ts
 * to the engagement reporter. Passing `undefined` clears the listener.
 */
export function setCommandInvocationListener(listener: (() => void) | undefined): void {
    commandInvocationListener = listener;
}

// ─────────────────────────────────────────────────────────────────────────────
// Test-only helpers
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Test seam: swap the singleton reporter with a fake. Returns a disposer that
 * restores the previous reporter. Intentionally not exported from the public
 * surface of the extension; only consumed by the in-process test suite.
 */
export function __setReporterForTests(fake: TelemetryReporter | undefined): () => void {
    const previous = reporter;
    reporter = fake;
    return () => { reporter = previous; };
}

/** Test seam: replace TelemetryReporter construction without initializing the real VS Code sender. */
export function __setTelemetryReporterFactoryForTests(factory: (aiKey: string) => TelemetryReporter): () => void {
    const previous = telemetryReporterFactory;
    telemetryReporterFactory = factory;
    return () => { telemetryReporterFactory = previous; };
}

/** Test seam: reset TelemetryReporter construction so tests don't bleed into each other. */
export function __resetTelemetryReporterFactoryForTests(): void {
    telemetryReporterFactory = defaultTelemetryReporterFactory;
}

/** Test seam: clear common properties so tests don't bleed into each other. */
export function __resetCommonPropertiesForTests(): void {
    for (const key of Object.keys(commonProperties) as CommonTelemetryProperty[]) {
        delete commonProperties[key];
    }
}
