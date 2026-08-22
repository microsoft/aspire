import { applyTextStyle } from '../utils/strings';
import {
    AppHostParentOutputFilter,
    isSevereRuntimeOutputLine,
    type AppHostParentOutput
} from './session/appHostParentOutputFilter';

export { AppHostParentOutputFilter };
export type { AppHostParentOutput };

const enum AnsiColors {
    Dim = '\x1b[2m',
    Yellow = '\x1b[33m',
}

export type AppHostLogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';

export interface AppHostLogEntry {
    sequenceNumber: number;
    logLevel: AppHostLogLevel;
    message: string;
    categoryName: string;
    eventId: number;
    exception?: string | null;
}

type LogSource = 'backchannel' | 'consoleLogger' | 'debugLogger';

interface LogRecord {
    categoryName: string;
    logLevel: AppHostLogLevel;
    eventId?: number;
    body: string;
    displayBody?: string;
    singleLine?: boolean;
}

interface LogRecordIdentity {
    record: LogRecord;
    leadingScopeBodyOffsets?: readonly number[];
    trailingBodyEndOffsets?: readonly number[];
}

interface LogRecordIdentityMatch {
    record: LogRecord;
    isExactBody: boolean;
}

interface CorrelatedRecord {
    identity: LogRecordIdentity;
    sources: Set<LogSource>;
}

interface PendingConsoleRecord {
    record: Omit<LogRecord, 'body'>;
    body: string;
    leadingScopeBodyOffsets: number[];
    raw: string;
    category: string;
    allowsContinuation: boolean;
    hasBodyLine: boolean;
    hasNonScopeBodyLine: boolean;
}

interface PendingDebugRecord {
    raw: string;
    category: string;
    hasException: boolean;
    ambiguousLineBoundaries: {
        rawOffset: number;
    }[];
}

export class AppHostLogOutputCoordinator {
    private static readonly _maxCorrelatedRecords = 1024;
    private static readonly _maxLowLevelCorrelatedRecords = 128;
    private static readonly _maxAmbiguousDebugLineBoundaries = 128;
    private static readonly _maxLeadingScopeBodyOffsets = 128;
    private static readonly _maxPendingDebugRecordCharacters = 64 * 1024;
    private static readonly _allSources: readonly LogSource[] = ['backchannel', 'consoleLogger', 'debugLogger'];
    private static readonly _lowLevelSources: readonly LogSource[] = ['consoleLogger', 'debugLogger'];
    private static readonly _maxRememberedBackchannelSequences = 1024;
    private static readonly _idleFlushDelayMs = 250;

    private readonly _correlatedRecords: CorrelatedRecord[] = [];
    private readonly _lowLevelCorrelatedRecords: CorrelatedRecord[] = [];
    private readonly _backchannelSequences = new Set<number>();
    private readonly _backchannelSequenceOrder: number[] = [];
    private readonly _partialLines = new Map<string, string>();
    private readonly _pendingRecords = new Map<string, PendingConsoleRecord>();
    private readonly _pendingDebugRecords = new Map<string, PendingDebugRecord>();
    private readonly _fallbackFilters = new Map<string, AppHostParentOutputFilter>();
    private readonly _idleFlushTimers = new Map<string, ReturnType<typeof setTimeout>>();

    constructor(
        private readonly _onIdleFlush?: (output: AppHostParentOutput) => void,
        private readonly _idleFlushDelayMs = AppHostLogOutputCoordinator._idleFlushDelayMs) {
    }

    handleBackchannelEntry(entry: AppHostLogEntry): AppHostParentOutput | undefined {
        if (entry.sequenceNumber > 0) {
            // A reconnect replays the AppHost's 1,000-entry buffer. Remember exact sequences
            // instead of a high-water mark so delayed delivery cannot discard an unseen record.
            if (this._backchannelSequences.has(entry.sequenceNumber)) {
                return undefined;
            }

            this._backchannelSequences.add(entry.sequenceNumber);
            this._backchannelSequenceOrder.push(entry.sequenceNumber);
            if (this._backchannelSequenceOrder.length > AppHostLogOutputCoordinator._maxRememberedBackchannelSequences) {
                this._backchannelSequences.delete(this._backchannelSequenceOrder.shift()!);
            }
        }

        const record = createBackchannelRecord(entry);

        return this.correlate({ record }, 'backchannel');
    }

    handleDebugAdapterOutput(output: string, category: string | undefined): AppHostParentOutput[] {
        const normalizedCategory = category ?? 'console';
        const outputs: AppHostParentOutput[] = [];

        const buffered = `${this._partialLines.get(normalizedCategory) ?? ''}${output}`;
        const lastBreak = findLastCompletedLineBreak(buffered);
        const completed = buffered.slice(0, lastBreak + 1);
        const partial = buffered.slice(lastBreak + 1);

        for (const line of completed.match(/[^\r\n]*(?:\r\n|\r|\n)/g) ?? []) {
            this.consumeLine(line, normalizedCategory, outputs);
        }

        if (partial) {
            this._partialLines.set(normalizedCategory, partial);
        } else {
            this._partialLines.delete(normalizedCategory);
        }

        this.scheduleIdleFlush(normalizedCategory);

        return outputs;
    }

    flush(): AppHostParentOutput[] {
        this.clearIdleFlushTimers();

        const outputs: AppHostParentOutput[] = [];
        const partials = [...this._partialLines];
        this._partialLines.clear();

        for (const [category, partial] of partials) {
            this.consumeLine(partial, category, outputs);
        }

        for (const category of [...this._pendingRecords.keys()]) {
            this.flushPendingRecord(category, outputs);
        }

        for (const category of [...this._pendingDebugRecords.keys()]) {
            this.flushPendingDebugRecord(category, outputs);
        }

        return outputs;
    }

    reset(): void {
        this.clearIdleFlushTimers();
        this._correlatedRecords.length = 0;
        this._lowLevelCorrelatedRecords.length = 0;
        this._backchannelSequences.clear();
        this._backchannelSequenceOrder.length = 0;
        this._partialLines.clear();
        this._pendingRecords.clear();
        this._pendingDebugRecords.clear();
        this._fallbackFilters.clear();
    }

    private consumeLine(line: string, category: string, outputs: AppHostParentOutput[]): void {
        if (category === 'console' && this.consumeDebugLoggerLine(line, category, outputs)) {
            return;
        }

        const pending = this._pendingRecords.get(category);
        if (pending) {
            const hasConsoleIndentation = isConsoleLoggerContinuation(line);
            if (pending.allowsContinuation && (hasConsoleIndentation || isWindowsBareLfContinuation(pending))) {
                pending.raw += line;
                const bodyLine = hasConsoleIndentation
                    ? removeConsoleIndentation(line)
                    : normalizeConsoleLine(line);
                // IncludeScopes writes leading lines such as:
                //   => RequestPath:/health => ConnectionId:0HN...
                // Keep them until correlation can distinguish scope metadata from a real message
                // such as `logger.LogInformation("=> started")`.
                pending.body += bodyLine;
                if (!pending.hasNonScopeBodyLine && bodyLine.startsWith('=> ')) {
                    // Each leading marker can be either scope metadata or the first message line.
                    // Store compact offsets instead of each suffix so deeply nested scopes retain
                    // linear state while `=> scope` followed by `=> message` remains distinguishable.
                    if (pending.leadingScopeBodyOffsets.length === AppHostLogOutputCoordinator._maxLeadingScopeBodyOffsets) {
                        pending.leadingScopeBodyOffsets.splice(1, 1);
                    }
                    pending.leadingScopeBodyOffsets.push(pending.body.length);
                } else {
                    pending.hasNonScopeBodyLine = true;
                }
                pending.hasBodyLine = true;
                return;
            }

            this.flushPendingRecord(category, outputs);
        }

        const multilineHeader = parseMultilineConsoleLoggerHeader(line);
        if (multilineHeader && category !== 'console') {
            this.resetFallbackFilter(category);
            this._pendingRecords.set(category, {
                record: multilineHeader,
                body: '',
                leadingScopeBodyOffsets: [],
                raw: line,
                category,
                allowsContinuation: true,
                hasBodyLine: false,
                hasNonScopeBodyLine: false
            });
            return;
        }

        const singleLineRecord = parseSingleLineConsoleLoggerRecord(line);
        if (singleLineRecord && category !== 'console') {
            this.resetFallbackFilter(category);
            this._pendingRecords.set(category, {
                record: {
                    categoryName: singleLineRecord.categoryName,
                    logLevel: singleLineRecord.logLevel,
                    eventId: singleLineRecord.eventId,
                    singleLine: true
                },
                body: singleLineRecord.body,
                leadingScopeBodyOffsets:
                    AppHostLogOutputCoordinator.getSingleLineScopeBodyOffsets(singleLineRecord.body),
                raw: line,
                category,
                allowsContinuation: false,
                hasBodyLine: true,
                hasNonScopeBodyLine: true
            });
            return;
        }

        this.emitFallback(line, category, outputs);
    }

    private flushPendingRecord(category: string, outputs: AppHostParentOutput[]): void {
        const pending = this._pendingRecords.get(category);
        if (!pending) {
            return;
        }

        this.clearIdleFlushTimer(category);
        this._pendingRecords.delete(category);

        const output = this.correlate(createPendingRecordIdentity(pending), 'consoleLogger');
        if (output) {
            outputs.push(output);
        }
    }

    private consumeDebugLoggerLine(
        line: string,
        category: string,
        outputs: AppHostParentOutput[]): boolean {
        const pending = this._pendingDebugRecords.get(category);
        if (pending) {
            if (pending.raw.length + line.length > AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
                // Keep the bounded record visible, then let later continuation lines follow the
                // normal console fallback policy rather than growing extension-host state forever.
                this.flushPendingDebugRecord(category, outputs);
                if (isDebugLoggerHeader(line)
                    && line.length <= AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
                    this._pendingDebugRecords.set(category, createPendingDebugRecord(line, category));
                    this.resetFallbackFilter(category);
                    return true;
                }
                return false;
            }

            if (isDebugLoggerHeader(line)) {
                if (this.mergedDebugRecordHasTwin(pending, line)) {
                    appendPendingDebugLine(pending, line);
                    return true;
                }

                this.flushPendingDebugRecord(category, outputs);
                this._pendingDebugRecords.set(category, createPendingDebugRecord(line, category));
                this.resetFallbackFilter(category);
                return true;
            }

            const isExceptionContinuation = isDebugLoggerExceptionContinuation(pending, line);
            if (startsUnrelatedDebuggerOutput(line) && !isExceptionContinuation) {
                // A severe-looking line can still be part of a multiline message. A provider
                // copy proves that case; otherwise remember the provisional full identity so a
                // delayed provider does not render the same logical record again after the split.
                if (this.mergedDebugRecordHasTwin(pending, line)) {
                    appendPendingDebugLine(pending, line);
                    return true;
                }
                const pendingRecord = parseDebugLoggerRecord(pending.raw);
                if (pendingRecord && !this.hasCorrelatedTwin(pendingRecord, 'debugLogger')) {
                    this.correlate({ record: pendingRecord }, 'debugLogger', false);
                }
                const provisionalRecord = parseDebugLoggerRecord(`${pending.raw}${line}`);
                if (provisionalRecord) {
                    this.correlate({ record: provisionalRecord }, 'debugLogger', false);
                }
                this.flushPendingDebugRecord(
                    category,
                    outputs,
                    true);
                return false;
            }

            if (!isDebugLoggerContinuation(pending, line)) {
                this.recordAmbiguousDebugLineBoundary(pending);
            }

            appendPendingDebugLine(pending, line);
            return true;
        }

        if (!isDebugLoggerHeader(line)
            || line.length > AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
            return false;
        }

        this._pendingDebugRecords.set(category, createPendingDebugRecord(line, category));
        this.resetFallbackFilter(category);
        return true;
    }

    private mergedDebugRecordHasTwin(pending: PendingDebugRecord, line: string): boolean {
        const merged = parseDebugLoggerRecord(`${pending.raw}${line}`);
        return !!merged && this.hasCorrelatedTwin(merged, 'debugLogger');
    }

    private recordAmbiguousDebugLineBoundary(pending: PendingDebugRecord): void {
        if (pending.ambiguousLineBoundaries.length === AppHostLogOutputCoordinator._maxAmbiguousDebugLineBoundaries) {
            // Keep the first possible boundary and the most recent ones. The first covers the
            // common one-line-message case; the rolling tail keeps long multiline messages
            // useful without allowing arbitrary console output to create unbounded scan work.
            pending.ambiguousLineBoundaries.splice(1, 1);
        }
        pending.ambiguousLineBoundaries.push({
            rawOffset: pending.raw.length
        });
    }

    private flushPendingDebugRecord(
        category: string,
        outputs: AppHostParentOutput[],
        hardBoundary = false): void {
        const pending = this._pendingDebugRecords.get(category);
        if (!pending) {
            return;
        }

        this.clearIdleFlushTimer(category);
        this._pendingDebugRecords.delete(category);

        const record = parseDebugLoggerRecord(pending.raw);
        if (!record) {
            this.emitFallback(pending.raw, pending.category, outputs);
            return;
        }

        if (hardBoundary && this.hasCorrelatedTwin(record, 'debugLogger')) {
            const output = this.correlate({ record }, 'debugLogger');
            if (output) {
                outputs.push(output);
            }
            return;
        }

        const matchingBoundary = !this.hasCorrelatedTwin(record, 'debugLogger')
            ? this.findConfirmedDebugLineBoundary(pending, record)
            : undefined;
        const selectedBoundary = hardBoundary
            ? matchingBoundary ?? pending.ambiguousLineBoundaries[0]
            : matchingBoundary;
        if (selectedBoundary) {
            const candidate = parseDebugLoggerRecord(pending.raw.slice(0, selectedBoundary.rawOffset));
            if (!candidate) {
                this.emitFallback(pending.raw, pending.category, outputs);
                return;
            }
            const output = this.correlate({ record: candidate }, 'debugLogger');
            if (output) {
                outputs.push(output);
            }
            const tail = pending.raw.slice(selectedBoundary.rawOffset);
            if (hardBoundary && !matchingBoundary) {
                // The boundary is conservative rather than provider-confirmed. Keep the
                // ambiguous middle visible before suppressing any delayed full provider copy.
                outputs.push({ output: tail, category: 'stdout' });
            } else {
                this.emitFallback(tail, pending.category, outputs);
            }
            return;
        }

        const trailingBodyEndOffsets = [...new Set(
            pending.ambiguousLineBoundaries
                .map(boundary => parseDebugLoggerRecord(pending.raw.slice(0, boundary.rawOffset))?.body.length)
                .filter((offset): offset is number => offset !== undefined && offset >= 0 && offset < record.body.length))];
        const identity = trailingBodyEndOffsets.length > 0
            ? { record, trailingBodyEndOffsets }
            : { record };
        const output = this.correlate(identity, 'debugLogger');
        if (output) {
            outputs.push(output);
        }
    }

    private findConfirmedDebugLineBoundary(
        pending: PendingDebugRecord,
        record: LogRecord): PendingDebugRecord['ambiguousLineBoundaries'][number] | undefined {
        // DebugLogger does not identify multiline message boundaries. Prefer the longest
        // candidate another provider has confirmed so only the remaining raw tail falls back.
        for (let index = pending.ambiguousLineBoundaries.length - 1; index >= 0; index--) {
            const boundary = pending.ambiguousLineBoundaries[index];
            const candidate = parseDebugLoggerRecord(pending.raw.slice(0, boundary.rawOffset));
            if (candidate && this.hasCorrelatedTwin(candidate, 'debugLogger')) {
                return boundary;
            }
        }

        return undefined;
    }

    private correlate(
        identity: LogRecordIdentity,
        source: LogSource,
        renderUnmatched = true): AppHostParentOutput | undefined {
        const records = this.correlatedRecordsFor(identity.record);
        let selectedMatch: { index: number; match: LogRecordIdentityMatch } | undefined;
        for (let index = 0; index < records.length; index++) {
            const candidate = records[index];
            if (candidate.sources.has(source)) {
                continue;
            }

            const match = matchRecordIdentities(candidate.identity, identity);
            if (match && (!selectedMatch || match.isExactBody && !selectedMatch.match.isExactBody)) {
                selectedMatch = { index, match };
                if (match.isExactBody) {
                    break;
                }
            }
        }

        if (!selectedMatch) {
            records.push({ identity, sources: new Set([source]) });
            const limit = isLowLevel(identity.record)
                ? AppHostLogOutputCoordinator._maxLowLevelCorrelatedRecords
                : AppHostLogOutputCoordinator._maxCorrelatedRecords;
            if (records.length > limit) {
                records.shift();
            }

            return renderUnmatched ? formatLogRecord(identity.record) : undefined;
        }

        const existing = records[selectedMatch.index];
        existing.sources.add(source);
        // Once another source identifies the actual message body, discard the other possible
        // scope boundaries so a later record cannot be suppressed through a rejected alias.
        existing.identity = { record: selectedMatch.match.record };

        const expectedSources = isLowLevel(selectedMatch.match.record)
            ? AppHostLogOutputCoordinator._lowLevelSources
            : AppHostLogOutputCoordinator._allSources;
        if (expectedSources.every(expectedSource => existing.sources.has(expectedSource))) {
            records.splice(selectedMatch.index, 1);
        }

        return undefined;
    }

    private hasCorrelatedTwin(record: LogRecord, source: LogSource): boolean {
        return this.correlatedRecordsFor(record).some(candidate =>
            !candidate.sources.has(source)
            && matchRecordIdentities(candidate.identity, { record }) !== undefined);
    }

    private correlatedRecordsFor(record: LogRecord): CorrelatedRecord[] {
        // Trace and Debug are not sent over the structured CLI backchannel. Keep their
        // adapter-only correlation history separate so a noisy low-level stream cannot
        // evict Information+ records that are still waiting for another provider copy.
        return isLowLevel(record) ? this._lowLevelCorrelatedRecords : this._correlatedRecords;
    }

    private emitFallback(output: string, category: string, outputs: AppHostParentOutput[]): void {
        const filtered = this.fallbackFilterFor(category).filter(output, category);
        if (filtered) {
            outputs.push(filtered);
        }
    }

    private fallbackFilterFor(category: string): AppHostParentOutputFilter {
        let filter = this._fallbackFilters.get(category);
        if (!filter) {
            filter = new AppHostParentOutputFilter();
            this._fallbackFilters.set(category, filter);
        }

        return filter;
    }

    private static getSingleLineScopeBodyOffsets(body: string): number[] {
        if (body === '=>') {
            return [body.length];
        }

        if (!body.startsWith('=> ')) {
            return [];
        }

        // SingleLine+IncludeScopes emits `=> scope message` with only a space between
        // the final scope and message, so retain each possible boundary until another
        // source confirms the exact body.
        const offsets: number[] = [];
        for (let offset = '=> '.length + 1; offset <= body.length; offset++) {
            if (offset < body.length && body[offset - 1] !== ' ') {
                continue;
            }

            if (offsets.length === AppHostLogOutputCoordinator._maxLeadingScopeBodyOffsets) {
                offsets.splice(1, 1);
            }
            offsets.push(offset);
        }

        return offsets;
    }

    private resetFallbackFilter(category: string): void {
        this._fallbackFilters.get(category)?.reset();
    }

    private scheduleIdleFlush(category: string): void {
        const pending = this._pendingRecords.get(category);
        const hasPendingDebugRecord = this._pendingDebugRecords.has(category);
        if (!this._onIdleFlush || (!pending && !hasPendingDebugRecord && !this._partialLines.has(category))) {
            this.clearIdleFlushTimer(category);
            return;
        }

        // Keep the deadline established by the first pending chunk. Restarting the timer
        // for every chunk can hide a continuously written partial line and grow it forever.
        if (this._idleFlushTimers.has(category)) {
            return;
        }

        const timer = setTimeout(() => {
            this._idleFlushTimers.delete(category);
            const outputs: AppHostParentOutput[] = [];

            const partial = this._partialLines.get(category);
            if (partial) {
                this._partialLines.delete(category);
                this.consumeLine(partial, category, outputs);
            }

            this.flushPendingRecord(category, outputs);
            this.flushPendingDebugRecord(category, outputs);
            outputs.forEach(output => this._onIdleFlush?.(output));
        }, this._idleFlushDelayMs);

        this._idleFlushTimers.set(category, timer);
    }

    private clearIdleFlushTimer(category: string): void {
        const timer = this._idleFlushTimers.get(category);
        if (timer) {
            clearTimeout(timer);
            this._idleFlushTimers.delete(category);
        }
    }

    private clearIdleFlushTimers(): void {
        for (const timer of this._idleFlushTimers.values()) {
            clearTimeout(timer);
        }
        this._idleFlushTimers.clear();
    }
}

function createBackchannelRecord(entry: AppHostLogEntry): LogRecord {
    const displayBody = normalizeLineEndings(joinRecordBody(entry.message, entry.exception));
    return {
        categoryName: escapeCategoryControlCharacters(entry.categoryName),
        logLevel: entry.logLevel,
        eventId: entry.eventId,
        body: normalizeRecordText(displayBody),
        displayBody
    };
}

function createPendingRecordIdentity(pending: PendingConsoleRecord): LogRecordIdentity {
    const record = {
        ...pending.record,
        body: normalizeRecordText(pending.body)
    };
    const leadingScopeBodyOffsets = [...new Set(
        pending.leadingScopeBodyOffsets
            .map(offset => Math.min(offset, record.body.length))
            .filter(offset => offset > 0))];

    return leadingScopeBodyOffsets.length > 0
        ? { record, leadingScopeBodyOffsets }
        : { record };
}

const consoleLoggerTimestampPrefix =
    String.raw`(?:(?:\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)\s+)?`;
const consoleLoggerAnsiSgrSequence = String.raw`\x1b\[[0-9;]*m`;
const consoleLoggerLevelPattern =
    String.raw`(?:${consoleLoggerAnsiSgrSequence})*(trce|dbug|info|warn|fail|crit)(?:${consoleLoggerAnsiSgrSequence})*`;
const multilineConsoleLoggerHeaderRegex = new RegExp(
    String.raw`^${consoleLoggerTimestampPrefix}${consoleLoggerLevelPattern}: (.*)\[(-?\d+)\](?:\r\n|\r|\n)$`);
const singleLineConsoleLoggerRecordRegex = new RegExp(
    String.raw`^${consoleLoggerTimestampPrefix}${consoleLoggerLevelPattern}: (.*?)\[(-?\d+)\] (.*?)(?:\r\n|\r|\n)?$`);
const debugLoggerCategoryPattern = String.raw`[A-Za-z_]\w*(?:\.\w+)+`;
const debugLoggerRecordRegex = new RegExp(
    String.raw`^(${debugLoggerCategoryPattern})(?:\[(-?\d+)\])?: (Trace|Debug|Information|Warning|Error|Critical): ([\s\S]*)$`);
const debugLoggerHeaderRegex = new RegExp(
    String.raw`^${debugLoggerCategoryPattern}(?:\[-?\d+\])?: (Trace|Debug|Information|Warning|Error|Critical): .*(?:\r\n|\r|\n)?$`);

function createPendingDebugRecord(line: string, category: string): PendingDebugRecord {
    return {
        raw: line,
        category,
        hasException: false,
        ambiguousLineBoundaries: []
    };
}

function appendPendingDebugLine(pending: PendingDebugRecord, line: string): void {
    if (isDebugLoggerExceptionStart(line.trim()) && endsWithBlankLine(pending.raw)) {
        pending.hasException = true;
    }
    pending.raw += line;
}

function parseMultilineConsoleLoggerHeader(line: string): Omit<LogRecord, 'body'> | undefined {
    // SimpleConsoleFormatter's default multiline record begins as:
    //   warn: Example.Category[7]
    //         First message line.
    // Common date/time prefixes are accepted, but arbitrary text before "warn:" is not:
    // otherwise a user line such as "status warn: ..." becomes a false log record.
    const match = multilineConsoleLoggerHeaderRegex.exec(line);
    if (!match) {
        return undefined;
    }

    return {
        categoryName: escapeCategoryControlCharacters(match[2]),
        logLevel: getFullLoggerLevel(match[1]),
        eventId: Number(match[3])
    };
}

function parseSingleLineConsoleLoggerRecord(line: string): LogRecord | undefined {
    // With SimpleConsoleFormatterOptions.SingleLine, the same record is:
    //   warn: Example.Category[7] First message line.
    const match = singleLineConsoleLoggerRecordRegex.exec(line);
    if (!match) {
        return undefined;
    }

    return {
        categoryName: escapeCategoryControlCharacters(match[2]),
        logLevel: getFullLoggerLevel(match[1]),
        eventId: Number(match[3]),
        body: normalizeRecordText(match[4]),
        singleLine: true
    };
}

function parseDebugLoggerRecord(output: string): LogRecord | undefined {
    // DebugLogger writes:
    //   Example.Category: Warning: Deployment failed.
    //
    //   System.InvalidOperationException: boom
    // It doesn't include the event ID, so correlation treats a missing ID as a wildcard
    // while still requiring category, level, and the complete normalized body to match.
    const normalized = normalizeRecordText(output.replace(/(?:\r\n|\r|\n)$/, ''));
    const match = debugLoggerRecordRegex.exec(normalized);
    if (!match) {
        return undefined;
    }

    const { message, exception } = splitMessageAndException(match[4]);
    return {
        categoryName: escapeCategoryControlCharacters(match[1]),
        logLevel: match[3] as AppHostLogLevel,
        eventId: match[2] === undefined ? undefined : Number(match[2]),
        body: normalizeRecordText(joinRecordBody(message, exception))
    };
}

function isDebugLoggerHeader(line: string): boolean {
    return debugLoggerHeaderRegex.test(line);
}

function isDebugLoggerContinuation(pending: PendingDebugRecord, line: string): boolean {
    const content = line.replace(/(?:\r\n|\r|\n)$/, '');
    const trimmedLine = content.trim();

    // DebugLogger continuation lines are ambiguous with arbitrary Debug.WriteLine output.
    // Exceptions are preceded by a blank separator; without it, an exception-shaped line is
    // unrelated runtime output and must retain its stderr classification.
    return !content
        || isDebugLoggerExceptionContinuation(pending, line)
        || /^\s/.test(content) && !isSevereRuntimeOutputLine(trimmedLine);
}

function isDebugLoggerExceptionContinuation(pending: PendingDebugRecord, line: string): boolean {
    const trimmedLine = line.trim();
    return isDebugLoggerExceptionStart(trimmedLine) && endsWithBlankLine(pending.raw)
        || (/^---> /.test(trimmedLine) || /^--- End of /.test(trimmedLine))
            && pending.hasException;
}

function startsUnrelatedDebuggerOutput(line: string): boolean {
    // DebugLogger continuation lines have no prefix, so only break on conservative,
    // debugger-owned shapes. Absorbing these lines would alter correlation identity and
    // could hide a fatal runtime line behind the preceding log record.
    const trimmedLine = line.trim();
    return isSevereRuntimeOutputLine(trimmedLine)
        || /^Unhandled exception\./.test(trimmedLine)
        || /^(?:'[^']*' \([^)]*\): |\S+ \(\d+\): )?Loaded '[^']*'\./.test(trimmedLine)
        || /^Exception thrown: '/.test(trimmedLine)
        || /^-{5,}$/.test(trimmedLine);
}

function endsWithBlankLine(value: string): boolean {
    return /(?:\r\n|\r|\n){2}$/.test(value.slice(-4));
}

function splitMessageAndException(value: string): { message: string; exception?: string } {
    const lines = value.replace(/\r\n|\r/g, '\n').split('\n');
    const exceptionIndex = lines.findIndex((line, index) =>
        index > 0 && lines[index - 1] === '' && isDebugLoggerExceptionStart(line));
    if (exceptionIndex < 0) {
        return { message: value };
    }

    return {
        message: lines.slice(0, exceptionIndex - 1).join('\n'),
        exception: lines.slice(exceptionIndex).join('\n')
    };
}

function isDebugLoggerExceptionStart(line: string): boolean {
    return /^(?:(?:[A-Za-z_][\w`]*\.)*[\w`]*(?:Exception|Error)(?: \([^)]*\))?:|Unhandled exception\.)/.test(line);
}

function isConsoleLoggerContinuation(line: string): boolean {
    const content = line.replace(/(?:\r\n|\r|\n)$/, '');
    return content.startsWith('      ');
}

function isWindowsBareLfContinuation(pending: PendingConsoleRecord): boolean {
    // On Windows SimpleConsoleFormatter only indents Environment.NewLine (`\r\n`).
    // A bare LF embedded in the message therefore leaves the following line unindented.
    return pending.raw.includes('\r\n')
        && pending.raw.endsWith('\n')
        && !pending.raw.endsWith('\r\n');
}

function removeConsoleIndentation(line: string): string {
    return line.slice(6).replace(/\r\n|\r/g, '\n');
}

function normalizeConsoleLine(line: string): string {
    return line.replace(/\r\n|\r/g, '\n');
}

function findLastCompletedLineBreak(text: string): number {
    // A Windows CRLF can be split between two DAP events. A trailing lone CR is therefore
    // incomplete until the next event supplies LF or the session flushes.
    const searchable = text.endsWith('\r') ? text.slice(0, -1) : text;
    return Math.max(searchable.lastIndexOf('\n'), searchable.lastIndexOf('\r'));
}

function matchRecordIdentities(left: LogRecordIdentity, right: LogRecordIdentity): LogRecordIdentityMatch | undefined {
    if (!recordHeadersMatch(left.record, right.record)) {
        return undefined;
    }

    if (recordBodiesMatchAt(left.record, 0, right.record)) {
        return {
            record: createCanonicalRecord(left.record, right.record),
            isExactBody: true
        };
    }

    const leftCandidate = findIdentityCandidateMatchingExactRecord(left, right.record);
    if (leftCandidate) {
        return {
            record: createCanonicalRecord(leftCandidate, right.record),
            isExactBody: false
        };
    }

    const rightCandidate = findIdentityCandidateMatchingExactRecord(right, left.record);
    if (rightCandidate) {
        return {
            record: createCanonicalRecord(left.record, rightCandidate),
            isExactBody: false
        };
    }

    const leftRanges = getAlternativeBodyRanges(left);
    if (leftRanges.length === 0) {
        return undefined;
    }

    const rightRanges = getAlternativeBodyRanges(right);
    if (rightRanges.length === 0) {
        return undefined;
    }

    const rightRangesByLength = new Map<number, BodyRange[]>();

    for (const rightRange of rightRanges) {
        const length = rightRange.end - rightRange.start;
        const ranges = rightRangesByLength.get(length);
        if (ranges) {
            ranges.push(rightRange);
        } else {
            rightRangesByLength.set(length, [rightRange]);
        }
    }

    for (const leftRange of leftRanges) {
        const matchingRightRanges = rightRangesByLength.get(leftRange.end - leftRange.start);
        for (const rightRange of matchingRightRanges ?? []) {
            if (recordBodyRangesMatch(
                left.record,
                leftRange.start,
                leftRange.end,
                right.record,
                rightRange.start,
                rightRange.end)) {
                return {
                    record: createCanonicalRecord(
                        createBodyRangeRecord(left.record, leftRange),
                        createBodyRangeRecord(right.record, rightRange)),
                    isExactBody: false
                };
            }
        }
    }

    return undefined;
}

interface BodyRange {
    start: number;
    end: number;
}

function findIdentityCandidateMatchingExactRecord(
    identity: LogRecordIdentity,
    exactRecord: LogRecord): LogRecord | undefined {
    for (const range of getAlternativeBodyRanges(identity)) {
        if (recordBodyRangesMatch(
            identity.record,
            range.start,
            range.end,
            exactRecord,
            0,
            exactRecord.body.length)) {
            return createBodyRangeRecord(identity.record, range);
        }
    }

    return undefined;
}

function getAlternativeBodyRanges(identity: LogRecordIdentity): BodyRange[] {
    const ranges = [
        ...(identity.leadingScopeBodyOffsets ?? []).map(start => ({
            start,
            end: identity.record.body.length
        })),
        ...(identity.trailingBodyEndOffsets ?? []).map(end => ({ start: 0, end }))
    ];
    return ranges.filter(range =>
        range.start >= 0
        && range.end <= identity.record.body.length
        && range.start <= range.end
        && (range.start > 0 || range.end < identity.record.body.length));
}

function createBodyRangeRecord(record: LogRecord, range: BodyRange): LogRecord {
    return {
        ...record,
        body: record.body.slice(range.start, range.end)
    };
}

function recordHeadersMatch(left: LogRecord, right: LogRecord): boolean {
    return left.categoryName === right.categoryName
        && left.logLevel === right.logLevel
        && (left.eventId === undefined || right.eventId === undefined || left.eventId === right.eventId);
}

function recordBodiesMatchAt(left: LogRecord, leftOffset: number, right: LogRecord): boolean {
    if (left.body.length - leftOffset !== right.body.length) {
        return false;
    }

    return recordBodyRangesMatch(
        left,
        leftOffset,
        left.body.length,
        right,
        0,
        right.body.length);
}

function recordBodyRangesMatch(
    left: LogRecord,
    leftStart: number,
    leftEnd: number,
    right: LogRecord,
    rightStart: number,
    rightEnd: number): boolean {
    if (leftEnd - leftStart !== rightEnd - rightStart) {
        return false;
    }

    if (!left.singleLine && !right.singleLine) {
        return left.body.startsWith(right.body.slice(rightStart, rightEnd), leftStart);
    }

    for (let index = 0; index < rightEnd - rightStart; index++) {
        const leftCharacter = left.body[leftStart + index] === '\n' ? ' ' : left.body[leftStart + index];
        const rightCharacter = right.body[rightStart + index] === '\n' ? ' ' : right.body[rightStart + index];
        if (leftCharacter !== rightCharacter) {
            return false;
        }
    }

    return true;
}

function createCanonicalRecord(
    left: LogRecord,
    right: LogRecord,
    bodyRecord = left.singleLine && !right.singleLine ? right : left): LogRecord {
    return {
        categoryName: bodyRecord.categoryName,
        logLevel: bodyRecord.logLevel,
        eventId: left.eventId ?? right.eventId,
        body: bodyRecord.body,
        displayBody: left.displayBody ?? right.displayBody,
        singleLine: left.singleLine && right.singleLine ? true : undefined
    };
}

function isLowLevel(record: LogRecord): boolean {
    return record.logLevel === 'Trace' || record.logLevel === 'Debug';
}

function formatLogRecord(record: LogRecord): AppHostParentOutput {
    const prefix = record.categoryName
        ? `${record.categoryName}: ${record.logLevel}`
        : record.logLevel;
    const raw = `${prefix}: ${record.displayBody ?? record.body}`;
    return formatRecord(raw, record.logLevel, record.logLevel === 'Error' || record.logLevel === 'Critical' ? 'stderr' : 'stdout');
}

function formatRecord(raw: string, logLevel: AppHostLogLevel, category: 'stdout' | 'stderr'): AppHostParentOutput {
    const style = logLevel === 'Trace' || logLevel === 'Debug'
        ? AnsiColors.Dim
        : logLevel === 'Warning'
            ? AnsiColors.Yellow
            : undefined;

    return {
        output: `${applyTextStyle(raw, style)}\n`,
        category
    };
}

function normalizeRecordText(value: string): string {
    return normalizeLineEndings(value).replace(/[ \t\n]+$/, '');
}

function normalizeLineEndings(value: string): string {
    return value.replace(/\r\n|\r/g, '\n');
}

function joinRecordBody(message: string, exception?: string | null): string {
    return [message, exception]
        .filter((part): part is string => !!part)
        .join('\n');
}

function escapeCategoryControlCharacters(value: string): string {
    return value.replace(/[\u0000-\u001f\u007f-\u009f]/g, character =>
        `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`);
}

function getFullLoggerLevel(shortLevel: string): AppHostLogLevel {
    switch (shortLevel) {
        case 'trce': return 'Trace';
        case 'dbug': return 'Debug';
        case 'info': return 'Information';
        case 'warn': return 'Warning';
        case 'fail': return 'Error';
        case 'crit': return 'Critical';
        default: throw new Error(`Unknown logger level: ${shortLevel}`);
    }
}
