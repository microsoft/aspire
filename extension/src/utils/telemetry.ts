import { TelemetryReporter } from '@vscode/extension-telemetry';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
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
// Aspire emits all telemetry through a single TelemetryReporter. We bypass
// VS Code's automatic `<extensionId>/<eventName>` prefix (added by
// `vscode.env.createTelemetryLogger`) by routing every event through
// `sendDangerousTelemetryEvent` / `sendDangerousTelemetryErrorEvent`, which
// reach the underlying sender without going through the prefix-applying
// logger. That gives us full control over the wire event name — the
// registry-declared names (e.g. `aspire/vscode/command/invoked`) ARE the
// names the telemetry backend sees.
//
// The "dangerous" variants skip the reporter's built-in telemetry-enabled
// gate, so we enforce it ourselves via `getCurrentTelemetryLevel()` below:
//   - regular events emit only when telemetry level === 'all'
//   - error events emit when level === 'all' or 'error'
//   - nothing emits when level is 'crash' or 'off'
// This mirrors what `@vscode/extension-telemetry` does for the non-dangerous
// path and matches `vscode.env.isTelemetryEnabled` for the regular channel.
//
// We keep the reporter as a module singleton because it is created at
// activation time and consumed from multiple places — the command wrapper,
// the engagement reporter, the tree view, the debug session, and the
// dashboard telemetry passthrough server.
let reporter: TelemetryReporter | undefined;
const telemetryReplacementOptions = [
    { lookup: /(?:^|_)(?:path|message|description|args?)(?:_|$)/i, replacementString: '<redacted>' },
];
const defaultTelemetryReporterFactory = (aiKey: string): TelemetryReporter => new TelemetryReporter(aiKey, telemetryReplacementOptions);
let telemetryReporterFactory = defaultTelemetryReporterFactory;
let reporterCommonProperties: Record<string, string> = {};

// Aspire-specific common properties merged into every event we emit (e.g.
// detected AppHost language, run mode). Keep this key set intentionally tiny
// and registered in `telemetryRegistry.ts` because each common property
// duplicates into a row per event in the classification catalog.
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
        reporterCommonProperties = getReporterCommonProperties(context);
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
 * Returns the reporter's currently observed telemetry level. The level is
 * computed by `@vscode/extension-telemetry` from the VS Code user setting
 * (`telemetry.telemetryLevel`) and reflects state transitions over time
 * (so a user toggling telemetry off mid-session is honored immediately).
 *
 *  - `'all'`   → both usage and error events allowed
 *  - `'error'` → only error events allowed (e.g. user selected "errors only")
 *  - `'crash'` → only crash events (no usage, no errors via this API)
 *  - `'off'`   → nothing allowed
 *
 * Returns `'off'` when the reporter has not been initialized (or has been
 * disposed) so the dangerous-send path is a no-op in tests and when the
 * extension's aiKey is absent.
 */
function getCurrentTelemetryLevel(): 'all' | 'error' | 'crash' | 'off' {
    return reporter?.telemetryLevel ?? 'off';
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
    return sanitizeTelemetryProperties({
        ...reporterCommonProperties,
        ...commonProperties,
        ...((properties ?? {}) as { [key: string]: string }),
    });
}

function getReporterCommonProperties(context: vscode.ExtensionContext): Record<string, string> {
    const properties: Record<string, string> = {
        'common.extname': context.extension.id,
        'common.extversion': String(context.extension.packageJSON.version ?? ''),
        'common.vscodemachineid': vscode.env.machineId,
        'common.vscodesessionid': vscode.env.sessionId,
        'common.vscodeversion': vscode.version,
        'common.os': os.platform(),
        'common.nodeArch': os.arch(),
        'common.platformversion': os.release().replace(/^(\d+)(\.\d+)?(\.\d+)?(.*)/, '$1$2$3'),
        'common.product': vscode.env.appHost,
        'common.uikind': getUiKind(),
        'common.remotename': vscode.env.remoteName ?? 'none',
        'common.isnewappinstall': String(vscode.env.isNewAppInstall),
        'common.telemetryclientversion': '1.5.1',
    };

    const commit = getVsCodeCommitHash();
    if (commit !== undefined) {
        properties['common.vscodecommithash'] = commit;
    }

    return properties;
}

function getUiKind(): string {
    switch (vscode.env.uiKind) {
        case vscode.UIKind.Desktop:
            return 'desktop';
        case vscode.UIKind.Web:
            return 'web';
        default:
            return String(vscode.env.uiKind);
    }
}

function getVsCodeCommitHash(): string | undefined {
    if (!vscode.env.appRoot) {
        return undefined;
    }

    const productJsonPath = path.join(vscode.env.appRoot, 'product.json');
    if (!fs.existsSync(productJsonPath)) {
        return undefined;
    }

    try {
        // This optional telemetry dimension must never block extension activation if VS Code's
        // product metadata is missing, unreadable, or malformed.
        const product = JSON.parse(fs.readFileSync(productJsonPath, 'utf8')) as { commit?: unknown };
        return typeof product.commit === 'string' ? product.commit : undefined;
    }
    catch {
        return undefined;
    }
}

function sanitizeTelemetryProperties(properties: Record<string, string>): Record<string, string> {
    const sanitized: Record<string, string> = {};
    for (const [key, value] of Object.entries(properties)) {
        sanitized[key] = sanitizeTelemetryValue(value, preservesStructuralTelemetryIds(key));
    }

    return sanitized;
}

function preservesStructuralTelemetryIds(key: string): boolean {
    return key === 'operation_id' ||
        key === 'asset_id' ||
        key === 'dashboard_correlated_with' ||
        key === 'common.vscodemachineid' ||
        key === 'common.vscodesessionid';
}

function sanitizeTelemetryValue(value: string, preserveGuids: boolean): string {
    const sanitized = redactHomeDirectories(value
        .replace(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, '<email>'))
        .replace(/\b(password|passwd|pwd|token|secret|sig|api[_-]?key|client[_-]?secret|account[_-]?key|shared[_-]?access[_-]?key|sharedaccesskey|connection[_-]?string|connectionstring|key)(\s*[:=]\s*)(?:(["'])([^"']*)\3|([^&\s"',;}]+))/gi, (_match: string, key: string, separator: string, quote: string | undefined) => `${key}${separator}${quote ?? ''}<redacted>${quote ?? ''}`)
        .replace(/([?&]sig=)(?:(["'])([^"']*)\2|([^&\s"',;}]+))/gi, (_match: string, prefix: string, quote: string | undefined) => `${prefix}${quote ?? ''}<redacted>${quote ?? ''}`)
        .replace(/\b(authorization\s*:\s*bearer\s+)[^\s"',;}]+/gi, '$1<redacted>')
        .replace(/\b(bearer\s+)[A-Za-z0-9._~+/=-]+/gi, '$1<redacted>');

    // GUID-shaped values can identify users, machines, or private cloud assets
    // when they appear in free-form fields. Keep dashboard correlation IDs
    // intact, though, because those structural fields are how start/end events
    // are joined downstream.
    if (preserveGuids) {
        return sanitized;
    }

    return sanitized.replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b/gi, '<guid>');
}

function redactHomeDirectories(value: string): string {
    const windowsPathSegment = '[^\\\\\\s"\':;,&|<>]+';
    const unixPathSegment = '[^/\\s"\':;,&|<>]+';
    const terminalUserName = '[^/\\\\\\s"\':;,&|<>-][^/\\\\\\s"\':;,&|<>]*(?: +[^/\\\\\\s"\':;,&|<>-][^/\\\\\\s"\':;,&|<>]*){0,3}?';
    const terminalBoundary = '(?=$|["\',;|]|\\s+--|\\s+-[A-Za-z0-9]|\\s+(?:&&|\\|\\||[|;,])|\\s+[A-Za-z_][A-Za-z0-9_.-]*=)';
    const windowsHomePattern = new RegExp(`\\b([A-Za-z]:)\\\\+Users\\\\+(?!<user>)(?:${windowsPathSegment}(?: +${windowsPathSegment})*(?=\\\\)|${terminalUserName}${terminalBoundary}|${windowsPathSegment})`, 'g');
    const macHomePattern = new RegExp(`(^|[^A-Za-z0-9_/-])/Users/(?!<user>)(?:${unixPathSegment}(?: +${unixPathSegment})*(?=/)|${terminalUserName}${terminalBoundary}|${unixPathSegment})`, 'g');
    const linuxHomePattern = new RegExp(`(^|[^A-Za-z0-9_/-])/home/(?!<user>)(?:${unixPathSegment}(?: +${unixPathSegment})*(?=/)|${terminalUserName}${terminalBoundary}|${unixPathSegment})`, 'g');

    return redactCurrentHomeDirectory(value)
        // Home-directory redaction. The username is a single path segment that can legitimately
        // contain spaces (e.g. `C:\Users\Alice Smith\project` or `/Users/Alice Smith/project`). Start
        // with the literal current home directory so command delimiters like `|`, `&&`, and free-form
        // words after a path do not confuse the generic best-effort patterns below. Then match either
        // a run of space-separated words that is still followed by the same-type path separator (the
        // username continues), a terminal path segment ending before a safe command boundary, OR a
        // single whitespace-free run (the historical behavior).
        .replace(/^([A-Za-z]:)\\+Users\\+[^\\\s"']+(?: +[^\\\s"']+)*$/g, (_, drive: string) => `${drive}\\Users\\<user>`)
        .replace(/^\/Users\/[^/\s"']+(?: +[^/\s"']+)*$/g, '/Users/<user>')
        .replace(/^\/home\/[^/\s"']+(?: +[^/\s"']+)*$/g, '/home/<user>')
        .replace(windowsHomePattern, (_match: string, drive: string) => `${drive}\\Users\\<user>`)
        .replace(macHomePattern, '$1/Users/<user>')
        .replace(linuxHomePattern, '$1/home/<user>');
}

function redactCurrentHomeDirectory(value: string): string {
    const homeDirectory = os.homedir().replace(/[\\/]+$/, '');
    const lastSeparatorIndex = Math.max(homeDirectory.lastIndexOf('/'), homeDirectory.lastIndexOf('\\'));
    if (lastSeparatorIndex <= 0) {
        return value;
    }

    const replacement = `${homeDirectory.slice(0, lastSeparatorIndex + 1)}<user>`;
    const flags = /^[A-Za-z]:[\\/]/.test(homeDirectory) ? 'gi' : 'g';

    return value.replace(new RegExp(`${escapeRegExp(homeDirectory)}(?=$|[\\\\/\\s"':;,&|()<>])`, flags), replacement);
}

function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Emit a telemetry event. The `eventName` is constrained to entries in
 * {@link KnownTelemetryEventName} (see telemetryRegistry.ts) and the
 * accepted `properties` / `measurements` keys are constrained to the per-event
 * union declared there. This prevents accidental introduction of new
 * (event, property) pairs that would need data classification.
 *
 * Routed through `sendDangerousTelemetryEvent` so the registry-declared event
 * name is what reaches the telemetry backend verbatim — VS Code's
 * `TelemetryLogger` would otherwise prepend `<extensionId>/` and turn
 * `aspire/vscode/command/invoked` into `microsoft-aspire.aspire-vscode/aspire/vscode/command/invoked`.
 * This path intentionally bypasses `TelemetryLogger.cleanData()`, so
 * {@link mergeProperties} applies our explicit value sanitizer before calling
 * the dangerous API.
 * Telemetry opt-in is enforced explicitly here (the dangerous API bypasses
 * the reporter's built-in gate) so we still respect the user's
 * `telemetry.telemetryLevel` setting and live changes to it.
 */
export function sendTelemetryEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    if (reporter === undefined) {
        return;
    }

    // Regular (non-error) events require full telemetry. Mirrors the gate
    // `sendTelemetryEvent` applies internally via `TelemetryLogger.logUsage`.
    if (getCurrentTelemetryLevel() !== 'all') {
        return;
    }
    reporter.sendDangerousTelemetryEvent(eventName, mergeProperties(properties), measurements as { [key: string]: number } | undefined);
}

/**
 * Emits an error telemetry event. Use for faults (unexpected exceptions,
 * dashboard fault posts, etc.) so the App Insights envelope is tagged as an
 * error while preserving the registry-declared event name verbatim.
 *
 * Routed through `sendDangerousTelemetryErrorEvent` for the same reason as
 * {@link sendTelemetryEvent}: VS Code's TelemetryLogger would otherwise add
 * an extension-id prefix to the wire event name. Error events emit when the
 * user has opted into 'error' OR 'all' (i.e. anything except 'crash' / 'off'),
 * matching the standard non-dangerous error API's gate. This path intentionally
 * bypasses `TelemetryLogger.cleanData()`, so {@link mergeProperties} applies
 * our explicit value sanitizer before calling the dangerous API.
 */
export function sendTelemetryErrorEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    if (reporter === undefined) {
        return;
    }

    const level = getCurrentTelemetryLevel();
    if (level !== 'all' && level !== 'error') {
        return;
    }
    reporter.sendDangerousTelemetryErrorEvent(eventName, mergeProperties(properties), measurements as { [key: string]: number } | undefined);
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
        const result = await Promise.resolve(fn());
        if (isHandledCommandFailure(result)) {
            outcome = 'error';
            errorKind = getHandledCommandFailureKind(result);
        }

        return result;
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
        const properties: EventProperties<'aspire/vscode/command/invoked'> = {
            command: commandName,
            outcome,
            ...(additionalProperties ?? {}),
        };
        if (errorKind) {
            properties.error_kind = errorKind;
        }
        sendTelemetryEvent('aspire/vscode/command/invoked', properties, { duration_ms: durationMs });
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

export function classifyError(err: unknown): string {
    if (err instanceof Error) {
        return normalizeErrorKind(err.name);
    }
    if (typeof err === 'string') {
        return 'String';
    }
    return typeof err;
}

function normalizeErrorKind(errorKind: string): string {
    return /^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(errorKind) ? errorKind : 'Error';
}

function isHandledCommandFailure(value: unknown): value is { success: false; errorKind?: unknown } {
    if (typeof value !== 'object' || value === null || !('success' in value)) {
        return false;
    }

    // Some command implementations report handled failures as return values so VS Code does not
    // also show its generic "command failed" notification. Keep those visible in command telemetry.
    return (value as { success?: unknown }).success === false;
}

function getHandledCommandFailureKind(value: { errorKind?: unknown }): string {
    return typeof value.errorKind === 'string' && value.errorKind.length > 0
        ? value.errorKind
        : 'HandledError';
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
    reporterCommonProperties = {};
}
