import * as os from 'os';
import * as vscode from 'vscode';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import {
    CommonTelemetryProperties,
    CommonTelemetryProperty,
    EventMeasurements,
    EventProperties,
    KnownTelemetryEventName,
    TelemetryPropertyValue,
} from './telemetryRegistry';

export type {
    CommonTelemetryProperties,
    CommonTelemetryProperty,
    EventMeasurements,
    EventProperties,
    KnownTelemetryEventName,
    TelemetryPropertyValue,
} from './telemetryRegistry';

type TelemetryReporterFactory = (aiKey: string, options: vscode.TelemetryLoggerOptions) => TelemetryReporter;
type TelemetryLoggerFactory = (sender: vscode.TelemetrySender, options: vscode.TelemetryLoggerOptions) => vscode.TelemetryLogger;

interface ReporterTelemetryData {
    properties?: Record<string, string | vscode.TelemetryTrustedValue<string> | undefined>;
    measurements?: Record<string, number | undefined>;
}

const defaultTelemetryReporterFactory: TelemetryReporterFactory = (aiKey, options) =>
    new TelemetryReporter(aiKey, undefined, options);
const defaultTelemetryLoggerFactory: TelemetryLoggerFactory = (sender, options) =>
    vscode.env.createTelemetryLogger(sender, options);

let reporter: TelemetryReporter | undefined;
let telemetryLogger: vscode.TelemetryLogger | undefined;
let telemetryReporterFactory: TelemetryReporterFactory = defaultTelemetryReporterFactory;
let telemetryLoggerFactory: TelemetryLoggerFactory = defaultTelemetryLoggerFactory;
const commonProperties: Partial<Record<CommonTelemetryProperty, string>> = {};
let commandInvocationListener: (() => void) | undefined;
const telemetryClientVersion = (require('@vscode/extension-telemetry/package.json') as { version: string }).version;

/**
 * Creates the telemetry reporter and the VS Code logger that owns opt-in
 * enforcement, privacy cleaning, common properties, and telemetry-output
 * logging. The transport sender strips VS Code's automatic extension-id prefix
 * only after those platform guarantees have been applied.
 *
 * VS Code applies that ordering in ExtHostTelemetryLogger.logEvent:
 * https://github.com/microsoft/vscode/blob/1.98.0/src/vs/workbench/api/common/extHostTelemetry.ts
 */
export function initializeTelemetry(context: vscode.ExtensionContext): void {
    if (telemetryLogger) {
        return;
    }

    const aiKey = context.extension.packageJSON.aiKey;
    if (!aiKey) {
        return;
    }

    // The reporter is transport-only, so its private logger must not also
    // capture extension-host exceptions. The outer logger below owns that path.
    const initializedReporter = telemetryReporterFactory(aiKey, { ignoreUnhandledErrors: true });
    const initializedLogger = telemetryLoggerFactory(
        createTransportSender(initializedReporter, context.extension.id),
        {
            // Preserve the extension's existing automatic exception telemetry.
            // Keeping the inner reporter opted out above ensures each exception
            // is cleaned by VS Code and forwarded through this bridge exactly once.
            ignoreUnhandledErrors: false,
            // @vscode/extension-telemetry normally adds these through its own
            // logger. We use the reporter only as a transport so the outer VS
            // Code logger needs the same package-specific common properties.
            additionalCommonProperties: getReporterAdditionalCommonProperties(),
        });

    reporter = initializedReporter;
    telemetryLogger = initializedLogger;

    context.subscriptions.push({
        dispose: async () => {
            initializedLogger.dispose();
            if (telemetryLogger === initializedLogger) {
                telemetryLogger = undefined;
            }
            if (reporter === initializedReporter) {
                reporter = undefined;
            }
            await initializedReporter.dispose();
        },
    });
}

function createTransportSender(initializedReporter: TelemetryReporter, extensionId: string): vscode.TelemetrySender {
    return {
        sendEventData(eventName, data) {
            const reporterData = data as ReporterTelemetryData | undefined;
            initializedReporter.sendDangerousTelemetryEvent(
                stripExtensionPrefix(eventName, extensionId),
                reporterData?.properties,
                reporterData?.measurements);
        },
        sendErrorData(error, data) {
            const reporterData = data as ReporterTelemetryData | undefined;
            // Caller-supplied telemetry arrives as `{ properties, measurements }`,
            // but VS Code's automatic `logError(Error)` path sends common properties
            // as a flat record. Preserve both shapes at the transport boundary.
            // https://github.com/microsoft/vscode/blob/1.98.0/src/vs/workbench/api/common/extHostTelemetry.ts
            const properties = reporterData && 'properties' in reporterData
                ? reporterData.properties
                : reporterData as ReporterTelemetryData['properties'];
            initializedReporter.sendDangerousTelemetryException(
                error,
                properties,
                reporterData?.measurements);
        },
    };
}

function stripExtensionPrefix(eventName: string, extensionId: string): string {
    const prefix = `${extensionId}/`;
    return eventName.startsWith(prefix) ? eventName.slice(prefix.length) : eventName;
}

function getReporterAdditionalCommonProperties(): Record<string, string> {
    return {
        'common.os': os.platform(),
        'common.nodeArch': os.arch(),
        'common.platformversion': os.release().replace(/^(\d+)(\.\d+)?(\.\d+)?(.*)/, '$1$2$3'),
        'common.telemetryclientversion': telemetryClientVersion,
    };
}

/**
 * Returns whether extension telemetry is enabled at either the usage or error
 * level. Actual transport remains owned by {@link vscode.TelemetryLogger}, so
 * VS Code can still keep events local in telemetry logging-only environments.
 */
export function isExtensionTelemetryEnabled(): boolean {
    return telemetryLogger?.isUsageEnabled === true || telemetryLogger?.isErrorsEnabled === true;
}

/**
 * Replace the current set of common properties. Passing `undefined` for a
 * property removes it. Values should already be bounded, non-PII summaries
 * (e.g. `csharp;typescript`, not project paths).
 */
export function setCommonTelemetryProperties(properties: CommonTelemetryProperties): void {
    for (const [key, value] of Object.entries(properties) as [CommonTelemetryProperty, string | undefined][]) {
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

function mergeProperties<E extends KnownTelemetryEventName>(
    eventProperties?: EventProperties<E>
): Record<string, TelemetryPropertyValue> {
    return {
        ...commonProperties,
        ...(eventProperties ?? {}),
    } as Record<string, TelemetryPropertyValue>;
}

/**
 * Emits a usage event through VS Code's telemetry logger. VS Code performs
 * telemetry-level gating, privacy cleaning, standard common-property injection,
 * and telemetry-output logging before the transport bridge removes the
 * `<extensionId>/` prefix from the final wire name.
 */
export function sendTelemetryEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    telemetryLogger?.logUsage(eventName, {
        properties: mergeProperties(properties),
        measurements,
    });
}

/**
 * Emits an error event through VS Code's telemetry logger so error-only opt-in
 * is respected independently from usage telemetry.
 */
export function sendTelemetryErrorEvent<E extends KnownTelemetryEventName>(
    eventName: E,
    properties?: EventProperties<E>,
    measurements?: EventMeasurements<E>
): void {
    telemetryLogger?.logError(eventName, {
        properties: mergeProperties(properties),
        measurements,
    });
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

export interface HandledCommandOutcome {
    readonly success: false;
    readonly canceled?: boolean;
    readonly errorKind?: string;
}

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
        if (isHandledCommandCancellation(result)) {
            outcome = 'canceled';
        }
        else if (isHandledCommandFailure(result)) {
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

function isHandledCommandFailure(value: unknown): value is HandledCommandOutcome {
    if (typeof value !== 'object' || value === null || !('success' in value)) {
        return false;
    }

    // Some command implementations report handled failures as return values so VS Code does not
    // also show its generic "command failed" notification. Keep those visible in command telemetry.
    return (value as { success?: unknown }).success === false;
}

function isHandledCommandCancellation(value: unknown): value is HandledCommandOutcome & { readonly canceled: true } {
    return isHandledCommandFailure(value) && value.canceled === true;
}

function getHandledCommandFailureKind(value: HandledCommandOutcome): string {
    return typeof value.errorKind === 'string' && value.errorKind.length > 0
        ? normalizeErrorKind(value.errorKind)
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

function createInjectedReporterLogger(fake: TelemetryReporter): vscode.TelemetryLogger {
    const changeEmitter = new vscode.EventEmitter<vscode.TelemetryLogger>();
    const logger: vscode.TelemetryLogger = {
        get isUsageEnabled() {
            return fake.telemetryLevel === 'all';
        },
        get isErrorsEnabled() {
            return fake.telemetryLevel === 'all' || fake.telemetryLevel === 'error';
        },
        logUsage(eventName, data) {
            if (!logger.isUsageEnabled) {
                return;
            }
            const reporterData = data as ReporterTelemetryData | undefined;
            fake.sendDangerousTelemetryEvent(
                eventName,
                cleanInjectedReporterProperties(reporterData?.properties),
                reporterData?.measurements
            );
        },
        logError(eventNameOrException, data) {
            if (!logger.isErrorsEnabled) {
                return;
            }
            const reporterData = data as ReporterTelemetryData | undefined;
            const properties = cleanInjectedReporterProperties(reporterData?.properties);
            if (typeof eventNameOrException === 'string') {
                fake.sendDangerousTelemetryErrorEvent(eventNameOrException, properties, reporterData?.measurements);
            }
            else {
                fake.sendDangerousTelemetryException(eventNameOrException, properties, reporterData?.measurements);
            }
        },
        onDidChangeEnableStates: changeEmitter.event,
        dispose() {
            changeEmitter.dispose();
        },
    };
    return logger;
}

function cleanInjectedReporterProperties(
    properties: ReporterTelemetryData['properties']
): Record<string, string> | undefined {
    if (properties === undefined) {
        return undefined;
    }

    const cleaned: Record<string, string> = {};
    for (const [key, value] of Object.entries(properties)) {
        if (value === undefined) {
            continue;
        }
        if (typeof value !== 'string') {
            cleaned[key] = value.value;
            continue;
        }

        // Keep this test seam aligned with the path-cleaning stage in VS Code
        // 1.98's cleanData implementation, including its relative-path match.
        // https://github.com/microsoft/vscode/blob/1.98.0/src/vs/platform/telemetry/common/telemetryUtils.ts
        const fileRegex = /(file:\/\/)?([a-zA-Z]:(\\\\|\\|\/)|(\\\\|\\|\/))?([\w-\._]+(\\\\|\\|\/))+[\w-\._]*/g;
        const nodeModulesRegex = /^[\\/]?(node_modules|node_modules\.asar)[\\/]/;
        const updatedValue = value.replaceAll('%20', ' ');
        let lastIndex = 0;
        let cleanedValue = '';
        for (const match of updatedValue.matchAll(fileRegex)) {
            if (!nodeModulesRegex.test(match[0])) {
                const matchIndex = match.index ?? 0;
                cleanedValue += updatedValue.slice(lastIndex, matchIndex) + '<REDACTED: user-file-path>';
                lastIndex = matchIndex + match[0].length;
            }
        }
        cleaned[key] = lastIndex === 0
            ? updatedValue
            : cleanedValue + updatedValue.slice(lastIndex);

        // Keep the test double aligned with the remaining secret-cleaning stage in VS Code 1.98.
        // https://github.com/microsoft/vscode/blob/1.98.0/src/vs/platform/telemetry/common/telemetryUtils.ts
        const secretPattern = injectedReporterUserDataRegexes.find(candidate => candidate.regex.test(cleaned[key]));
        if (secretPattern) {
            cleaned[key] = `<REDACTED: ${secretPattern.label}>`;
        }
    }

    return cleaned;
}

const injectedReporterUserDataRegexes = [
    { label: 'Google API Key', regex: /AIza[A-Za-z0-9_\\-]{35}/ },
    { label: 'Slack Token', regex: /xox[pbar]-[A-Za-z0-9]/ },
    { label: 'GitHub Token', regex: /(gh[psuro]_[a-zA-Z0-9]{36}|github_pat_[a-zA-Z0-9]{22}_[a-zA-Z0-9]{59})/ },
    { label: 'Generic Secret', regex: /(key|token|sig|secret|signature|password|passwd|pwd|android:value)[^a-zA-Z0-9]/i },
    { label: 'CLI Credentials', regex: /\b(?:login|psexec|certutil(?:\.exe)?|net(?:\.exe)?\s+(?:user|share)|user\s+-?\s*secrets\s+set)\b/i },
    { label: 'Microsoft Entra ID', regex: /eyJ(?:0eXAiOiJKV1Qi|hbGci|[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.)/ },
    { label: 'Email', regex: /@[a-zA-Z0-9-]+\.[a-zA-Z0-9-]+/ },
] as const;

/**
 * Test seam: swap the singleton reporter with a fake and route through a small
 * logger double. Returns a disposer that restores the previous state.
 */
export function __setReporterForTests(fake: TelemetryReporter | undefined): () => void {
    const previousReporter = reporter;
    const previousLogger = telemetryLogger;
    const injectedLogger = fake ? createInjectedReporterLogger(fake) : undefined;
    reporter = fake;
    telemetryLogger = injectedLogger;
    return () => {
        injectedLogger?.dispose();
        if (reporter === fake) {
            reporter = previousReporter;
        }
        if (telemetryLogger === injectedLogger) {
            telemetryLogger = previousLogger;
        }
    };
}

/** Test seam: replace TelemetryReporter construction without initializing the real transport. */
export function __setTelemetryReporterFactoryForTests(factory: TelemetryReporterFactory): () => void {
    const previous = telemetryReporterFactory;
    telemetryReporterFactory = factory;
    return () => { telemetryReporterFactory = previous; };
}

/** Test seam: reset TelemetryReporter construction so tests don't bleed into each other. */
export function __resetTelemetryReporterFactoryForTests(): void {
    telemetryReporterFactory = defaultTelemetryReporterFactory;
}

/** Test seam: replace VS Code TelemetryLogger creation with a deterministic logger double. */
export function __setTelemetryLoggerFactoryForTests(factory: TelemetryLoggerFactory): () => void {
    const previous = telemetryLoggerFactory;
    telemetryLoggerFactory = factory;
    return () => { telemetryLoggerFactory = previous; };
}

/** Test seam: reset VS Code TelemetryLogger creation so tests don't bleed into each other. */
export function __resetTelemetryLoggerFactoryForTests(): void {
    telemetryLoggerFactory = defaultTelemetryLoggerFactory;
}

/** Test seam: clear common properties so tests don't bleed into each other. */
export function __resetCommonPropertiesForTests(): void {
    for (const key of Object.keys(commonProperties) as CommonTelemetryProperty[]) {
        delete commonProperties[key];
    }
}
