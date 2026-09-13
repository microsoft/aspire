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
    generationId?: string;
    sequenceNumber: number;
    logLevel: AppHostLogLevel;
    message: string;
    categoryName: string;
    eventId: number;
    exception?: string | null;
}

type LogSource = 'backchannel' | 'consoleLogger' | 'debugLogger';
const maxSingleLineRecordAlternatives = 128;
const maxDebugLoggerRecordAlternatives = 128;

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
    alternativeRecords?: readonly LogRecord[];
    trailingBoundaryRecords?: readonly LogRecord[];
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
    alternativeRecords: LogRecord[];
    leadingScopeBodyOffsets: number[];
    raw: string;
    category: string;
    allowsContinuation: boolean;
    hasBodyLine: boolean;
    hasNonScopeBodyLine: boolean;
    overflowed: boolean;
    hasCrLf: boolean;
    endsWithBareLf: boolean;
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
    private static readonly _maxDebugHeaderCandidateComparisons = 256;
    private static readonly _allSources: readonly LogSource[] = ['backchannel', 'consoleLogger', 'debugLogger'];
    private static readonly _lowLevelSources: readonly LogSource[] = ['consoleLogger', 'debugLogger'];
    // Match BackchannelLoggerProvider's 1,000-entry replay buffer.
    private static readonly _maxRememberedBackchannelSequences = 1000;
    private static readonly _idleFlushDelayMs = 250;

    private readonly _correlatedRecords: CorrelatedRecord[] = [];
    private readonly _lowLevelCorrelatedRecords: CorrelatedRecord[] = [];
    private readonly _backchannelSequences = new Set<string>();
    private readonly _backchannelSequenceOrder: string[] = [];
    private readonly _partialLines = new Map<string, string>();
    private readonly _pendingConsoleHeaderFragments = new Map<string, string>();
    private readonly _pendingRecords = new Map<string, PendingConsoleRecord>();
    private readonly _pendingDebugRecords = new Map<string, PendingDebugRecord>();
    private readonly _fallbackFilters = new Map<string, AppHostParentOutputFilter>();
    private readonly _idleFlushTimers = new Map<string, ReturnType<typeof setTimeout>>();
    private _lastDebugAdapterCategory: string | undefined;

    constructor(
        private readonly _onIdleFlush?: (output: AppHostParentOutput) => void,
        private readonly _idleFlushDelayMs = AppHostLogOutputCoordinator._idleFlushDelayMs) {
    }

    handleBackchannelEntry(entry: AppHostLogEntry): AppHostParentOutput | undefined {
        if (entry.sequenceNumber > 0) {
            // A reconnect replays the AppHost's 1,000-entry buffer. Remember exact generation/sequence
            // pairs so delayed delivery is safe and a replacement AppHost can reuse its counter.
            const sequenceIdentity = `${entry.generationId ?? ''}\0${entry.sequenceNumber}`;
            if (this._backchannelSequences.has(sequenceIdentity)) {
                return undefined;
            }

            this._backchannelSequences.add(sequenceIdentity);
            this._backchannelSequenceOrder.push(sequenceIdentity);
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

        if (normalizedCategory !== this._lastDebugAdapterCategory) {
            const previousCategory = this._lastDebugAdapterCategory;
            if (previousCategory) {
                const partial = this._partialLines.get(previousCategory);
                const parserOwnsPartial = this._pendingConsoleHeaderFragments.has(previousCategory)
                    || this._pendingRecords.has(previousCategory)
                    || this._pendingDebugRecords.has(previousCategory);
                if (partial && !parserOwnsPartial) {
                    this._partialLines.delete(previousCategory);
                    this.consumeLine(partial, previousCategory, outputs);
                }
                this.resetFallbackFilter(previousCategory);
            }
            this._lastDebugAdapterCategory = normalizedCategory;
        }

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

        for (const category of [...this._pendingConsoleHeaderFragments.keys()]) {
            this.flushPendingConsoleHeaderFragment(category, outputs);
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
        this._pendingConsoleHeaderFragments.clear();
        this._pendingRecords.clear();
        this._pendingDebugRecords.clear();
        this._fallbackFilters.clear();
        this._lastDebugAdapterCategory = undefined;
    }

    private consumeLine(line: string, category: string, outputs: AppHostParentOutput[]): void {
        if (category === 'console' && this.consumeDebugLoggerLine(line, category, outputs)) {
            return;
        }

        const headerFragment = this._pendingConsoleHeaderFragments.get(category);
        if (headerFragment) {
            if (this.tryStartConsoleLoggerRecord(line, category)) {
                this._pendingConsoleHeaderFragments.delete(category);
                this.emitFallback(headerFragment, category, outputs);
                this.resetFallbackFilter(category);
                return;
            }

            const combined = `${headerFragment}${line}`;
            if (combined.length <= AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
                if (this.tryStartConsoleLoggerRecord(combined, category)) {
                    this._pendingConsoleHeaderFragments.delete(category);
                    return;
                }

                this._pendingConsoleHeaderFragments.set(category, combined);
                return;
            }

            this._pendingConsoleHeaderFragments.delete(category);
            this.emitFallback(headerFragment, category, outputs);
        }

        const pending = this._pendingRecords.get(category);
        if (pending) {
            const hasConsoleIndentation =
                pending.record.singleLine !== true && isConsoleLoggerContinuation(line);
            if (pending.allowsContinuation && (hasConsoleIndentation || isWindowsBareLfContinuation(pending, line))) {
                if (pending.overflowed) {
                    this.emitRawConsoleLoggerOutput(line, pending.category, pending.record.logLevel, outputs);
                    updatePendingConsoleLineEnding(pending, line);
                    return;
                }

                if (pending.raw.length + line.length > AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
                    this.emitRawConsoleLoggerOutput(
                        pending.raw,
                        pending.category,
                        pending.record.logLevel,
                        outputs);
                    this.emitRawConsoleLoggerOutput(
                        line,
                        pending.category,
                        pending.record.logLevel,
                        outputs);
                    pending.body = '';
                    pending.alternativeRecords.length = 0;
                    pending.leadingScopeBodyOffsets.length = 0;
                    pending.overflowed = true;
                    updatePendingConsoleLineEnding(pending, line);
                    return;
                }

                pending.raw += line;
                updatePendingConsoleLineEnding(pending, line);
                const bodyLine = hasConsoleIndentation
                    ? removeConsoleIndentation(line)
                    : `${pending.record.singleLine && !pending.body.endsWith('\n') ? '\n' : ''}`
                        + normalizeConsoleLine(line);
                // IncludeScopes writes leading lines such as:
                //   => RequestPath:/health => ConnectionId:0HN...
                // Keep them until correlation can distinguish scope metadata from a real message
                // such as `logger.LogInformation("=> started")`.
                pending.body += bodyLine;
                for (const alternativeRecord of pending.alternativeRecords) {
                    alternativeRecord.body += bodyLine;
                }
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

        if (this.tryStartConsoleLoggerRecord(line, category)) {
            return;
        }

        if (category !== 'console'
            && line.length > AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
            const record = parseMultilineConsoleLoggerHeader(line) ?? parseSingleLineConsoleLoggerRecord(line);
            if (record) {
                this.emitRawConsoleLoggerOutput(line, category, record.logLevel, outputs);
                return;
            }
        }

        if (category !== 'console'
            && line.length <= AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters
            && singleLineConsoleLoggerPrefixRegex.test(line)) {
            this._pendingConsoleHeaderFragments.set(category, line);
            return;
        }

        this.emitFallback(line, category, outputs);
    }

    private tryStartConsoleLoggerRecord(line: string, category: string): boolean {
        if (category === 'console'
            || line.length > AppHostLogOutputCoordinator._maxPendingDebugRecordCharacters) {
            return false;
        }

        const multilineHeader = parseMultilineConsoleLoggerHeader(line);
        if (multilineHeader) {
            this.resetFallbackFilter(category);
            this._pendingRecords.set(category, {
                record: multilineHeader,
                body: '',
                alternativeRecords: [],
                leadingScopeBodyOffsets: [],
                raw: line,
                category,
                allowsContinuation: true,
                hasBodyLine: false,
                hasNonScopeBodyLine: false,
                overflowed: false,
                hasCrLf: line.includes('\r\n'),
                endsWithBareLf: line.endsWith('\n') && !line.endsWith('\r\n')
            });
            return true;
        }

        const singleLinePending = AppHostLogOutputCoordinator.createPendingSingleLineRecord(line, category);
        if (!singleLinePending) {
            return false;
        }

        this.resetFallbackFilter(category);
        this._pendingRecords.set(category, singleLinePending);
        return true;
    }

    private flushPendingConsoleHeaderFragment(
        category: string,
        outputs: AppHostParentOutput[]): void {
        const fragment = this._pendingConsoleHeaderFragments.get(category);
        if (!fragment) {
            return;
        }

        this._pendingConsoleHeaderFragments.delete(category);
        this.emitFallback(fragment, category, outputs);
    }

    private flushPendingRecord(category: string, outputs: AppHostParentOutput[]): void {
        const pending = this._pendingRecords.get(category);
        if (!pending) {
            return;
        }

        this.clearIdleFlushTimer(category);
        this._pendingRecords.delete(category);

        if (pending.overflowed) {
            return;
        }

        const identity = createPendingRecordIdentity(pending);
        if (hasUnconfirmedSingleLineContinuations(pending)
            && !this.hasCorrelatedIdentityTwin(identity, 'consoleLogger')) {
            const firstLine = getFirstLine(pending.raw);
            const firstPending = AppHostLogOutputCoordinator.createPendingSingleLineRecord(firstLine, category)!;
            const firstOutput = this.correlate(createPendingRecordIdentity(firstPending), 'consoleLogger');
            if (firstOutput) {
                outputs.push(firstOutput);
            }
            this.emitFallback(pending.raw.slice(firstLine.length), category, outputs);
            return;
        }

        const output = this.correlate(identity, 'consoleLogger');
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
                // DebugLogger does not mark multiline message boundaries. A continuation can itself
                // have the shape of another record, so retain both interpretations until another
                // provider confirms whether this is a continuation or a separate record.
                this.recordAmbiguousDebugLineBoundary(pending);
                appendPendingDebugLine(pending, line);
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
                const pendingIdentity = createDebugLoggerIdentity(pending.raw);
                if (pendingIdentity && !this.hasCorrelatedIdentityTwin(pendingIdentity, 'debugLogger')) {
                    this.correlate(pendingIdentity, 'debugLogger', false);
                }
                const provisionalIdentity = createDebugLoggerIdentity(`${pending.raw}${line}`);
                if (provisionalIdentity) {
                    this.correlate(provisionalIdentity, 'debugLogger', false);
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
        const headerIdentity = createDebugLoggerIdentity(getFirstLine(pending.raw));
        const hasPotentialTwin = !!headerIdentity
            && this.correlatedRecordCollectionsFor(headerIdentity).some(records =>
                records.some(candidate =>
                    !candidate.sources.has('debugLogger')
                    && getIdentityRecords(candidate.identity).some(candidateRecord =>
                        getIdentityRecords(headerIdentity).some(headerRecord =>
                            recordHeadersMatch(candidateRecord, headerRecord)))));
        if (!hasPotentialTwin) {
            return false;
        }

        const mergedIdentity = createDebugLoggerIdentity(`${pending.raw}${line}`);
        return !!mergedIdentity && this.hasCorrelatedIdentityTwin(mergedIdentity, 'debugLogger');
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

        const identity = createDebugLoggerIdentity(pending.raw);
        if (!identity) {
            this.emitFallback(pending.raw, pending.category, outputs);
            return;
        }

        const record = identity.record;
        const hasCorrelatedTwin = this.hasCorrelatedIdentityTwin(identity, 'debugLogger');
        if (hasCorrelatedTwin) {
            const output = this.correlate(identity, 'debugLogger');
            if (output) {
                outputs.push(output);
            }
            return;
        }

        const boundaries = getAmbiguousDebugLineBoundaries(pending.raw);
        const headerBoundaries = boundaries.filter(boundary =>
            isDebugLoggerHeader(getFirstLine(pending.raw, boundary.rawOffset)));
        if (headerBoundaries.length > 0) {
            this.flushDebugHeaderSegments(pending, boundaries, headerBoundaries, outputs);
            return;
        }

        const matchingBoundary = this.findConfirmedDebugLineBoundary(pending);
        const selectedBoundary = hardBoundary
            ? matchingBoundary ?? pending.ambiguousLineBoundaries[0]
            : matchingBoundary;
        if (selectedBoundary) {
            const candidateIdentity = createDebugLoggerIdentity(
                pending.raw.slice(0, selectedBoundary.rawOffset));
            if (!candidateIdentity) {
                this.emitFallback(pending.raw, pending.category, outputs);
                return;
            }
            const output = this.correlate(candidateIdentity, 'debugLogger');
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

        const finalIdentity = addDebugLoggerTrailingAliases(
            identity,
            pending.raw,
            pending.ambiguousLineBoundaries);
        const output = this.correlate(finalIdentity, 'debugLogger');
        if (output) {
            outputs.push(output);
        }
    }

    private findConfirmedDebugLineBoundary(
        pending: PendingDebugRecord): PendingDebugRecord['ambiguousLineBoundaries'][number] | undefined {
        // DebugLogger does not identify multiline message boundaries. Prefer the longest
        // candidate another provider has confirmed so only the remaining raw tail falls back.
        for (let index = pending.ambiguousLineBoundaries.length - 1; index >= 0; index--) {
            const boundary = pending.ambiguousLineBoundaries[index];
            const candidateIdentity = createDebugLoggerIdentity(
                pending.raw.slice(0, boundary.rawOffset));
            if (candidateIdentity && this.hasCorrelatedIdentityTwin(candidateIdentity, 'debugLogger')) {
                return boundary;
            }
        }

        return undefined;
    }

    private findConfirmedDebugHeaderSegmentEndIndex(
        raw: string,
        startOffset: number,
        endOffsets: readonly number[],
        firstEndIndex: number,
        providerBodiesByHeader: Map<string, { bodies: ReadonlySet<string>; maxBodyLength: number }>,
        comparisonBudget: { remaining: number }): number | undefined {
        const headerIdentity = createDebugLoggerIdentity(getFirstLine(raw, startOffset));
        if (!headerIdentity) {
            return undefined;
        }

        const getProviderBodies = (record: LogRecord): { bodies: ReadonlySet<string>; maxBodyLength: number } => {
            const cacheKey = `${record.categoryName}\0${record.logLevel}`;
            const cached = providerBodiesByHeader.get(cacheKey);
            if (cached) {
                return cached;
            }

            const bodies = new Set<string>();
            for (const records of this.correlatedRecordCollectionsFor({ record })) {
                for (const candidate of records) {
                    if (candidate.sources.has('debugLogger')) {
                        continue;
                    }
                    for (const candidateRecord of getIdentityRecords(candidate.identity)) {
                        if (recordHeadersMatch(candidateRecord, record)) {
                            bodies.add(candidateRecord.body);
                        }
                    }
                    if (recordHeadersMatch(candidate.identity.record, record)) {
                        for (const range of getAlternativeBodyRanges(candidate.identity)) {
                            bodies.add(candidate.identity.record.body.slice(range.start, range.end));
                        }
                    }
                }
            }
            let maxBodyLength = 0;
            for (const body of bodies) {
                maxBodyLength = Math.max(maxBodyLength, body.length);
            }
            const providerBodies = {
                bodies,
                maxBodyLength
            };
            providerBodiesByHeader.set(cacheKey, providerBodies);
            return providerBodies;
        };

        if (!getIdentityRecords(headerIdentity).some(record => getProviderBodies(record).bodies.size > 0)) {
            return undefined;
        }

        let confirmedEndIndex: number | undefined;
        for (let endIndex = firstEndIndex; endIndex < endOffsets.length; endIndex++) {
            if (comparisonBudget.remaining === 0) {
                break;
            }

            comparisonBudget.remaining--;
            const candidateIdentity = createDebugLoggerIdentity(
                raw.slice(startOffset, endOffsets[endIndex]));
            if (!candidateIdentity) {
                break;
            }
            if (getIdentityRecords(candidateIdentity).some(candidate => {
                const providerBodies = getProviderBodies(candidate);
                return candidate.body.length <= providerBodies.maxBodyLength
                    && providerBodies.bodies.has(candidate.body);
            })) {
                confirmedEndIndex = endIndex;
            }
        }

        return confirmedEndIndex;
    }

    private flushDebugHeaderSegments(
        pending: PendingDebugRecord,
        boundaries: readonly { rawOffset: number }[],
        headerBoundaries: readonly { rawOffset: number }[],
        outputs: AppHostParentOutput[]): void {
        const fullIdentity = createDebugLoggerIdentity(pending.raw);
        const segmentOffsets = [0, ...headerBoundaries.map(boundary => boundary.rawOffset), pending.raw.length];
        const providerBodiesByHeader =
            new Map<string, { bodies: ReadonlySet<string>; maxBodyLength: number }>();
        const comparisonBudget = {
            remaining: AppHostLogOutputCoordinator._maxDebugHeaderCandidateComparisons
        };
        let segmentIndex = 0;
        let boundaryIndex = 0;
        while (segmentIndex < segmentOffsets.length - 1) {
            const startOffset = segmentOffsets[segmentIndex];
            const confirmedEndIndex = this.findConfirmedDebugHeaderSegmentEndIndex(
                pending.raw,
                startOffset,
                segmentOffsets,
                segmentIndex + 1,
                providerBodiesByHeader,
                comparisonBudget);
            const endIndex = confirmedEndIndex ?? segmentIndex + 1;
            const endOffset = segmentOffsets[endIndex];
            const segment = pending.raw.slice(startOffset, endOffset);
            const recordIdentity = createDebugLoggerIdentity(segment);
            const record = recordIdentity?.record;

            while (boundaryIndex < boundaries.length
                && boundaries[boundaryIndex].rawOffset <= startOffset) {
                boundaryIndex++;
            }

            const segmentBoundaries: { rawOffset: number }[] = [];
            while (boundaryIndex < boundaries.length
                && boundaries[boundaryIndex].rawOffset < endOffset) {
                segmentBoundaries.push({
                    rawOffset: boundaries[boundaryIndex].rawOffset - startOffset
                });
                boundaryIndex++;
            }

            if (!record) {
                this.emitFallback(segment, pending.category, outputs);
            } else {
                let identity = addDebugLoggerTrailingAliases(
                    recordIdentity,
                    segment,
                    segmentBoundaries);
                if (segmentIndex === 0 && fullIdentity) {
                    identity = addDebugLoggerTrailingAliases(
                        fullIdentity,
                        pending.raw,
                        [{ rawOffset: endOffset }, ...pending.ambiguousLineBoundaries]);
                    identity = {
                        ...identity,
                        trailingBodyEndOffsets: [...new Set([
                            ...(identity.trailingBodyEndOffsets ?? []),
                            record.body.length
                        ])]
                    };
                }
                const hasAlternativeBody = getAlternativeBodyRanges(identity).length > 0
                    || (identity.alternativeRecords?.length ?? 0) > 0;
                const output = confirmedEndIndex === undefined && !hasAlternativeBody
                    ? this.rememberUnmatched(identity, 'debugLogger', record)
                    : this.correlate(identity, 'debugLogger', true, record);
                if (output) {
                    outputs.push(output);
                } else {
                    // Correlation consumes this source occurrence. Rebuild the lookup before
                    // selecting another group so one provider copy cannot confirm two records.
                    providerBodiesByHeader.clear();
                }
            }

            segmentIndex = endIndex;
        }
    }

    private rememberUnmatched(
        identity: LogRecordIdentity,
        source: LogSource,
        record = identity.record): AppHostParentOutput {
        const records = this.correlatedRecordsFor(identity.record);
        records.push({ identity, sources: new Set([source]) });
        const limit = isLowLevel(identity.record)
            ? AppHostLogOutputCoordinator._maxLowLevelCorrelatedRecords
            : AppHostLogOutputCoordinator._maxCorrelatedRecords;
        if (records.length > limit) {
            records.shift();
        }

        return formatLogRecord(record);
    }

    private correlate(
        identity: LogRecordIdentity,
        source: LogSource,
        renderUnmatched = true,
        unmatchedRecord = identity.record): AppHostParentOutput | undefined {
        let selectedMatch: {
            records: CorrelatedRecord[];
            index: number;
            match: LogRecordIdentityMatch;
        } | undefined;
        search:
        for (const records of this.correlatedRecordCollectionsFor(identity)) {
            for (let index = 0; index < records.length; index++) {
                const candidate = records[index];
                if (candidate.sources.has(source)) {
                    continue;
                }

                const match = matchRecordIdentities(candidate.identity, identity);
                if (match && (!selectedMatch || match.isExactBody && !selectedMatch.match.isExactBody)) {
                    selectedMatch = { records, index, match };
                    if (match.isExactBody) {
                        break search;
                    }
                }
            }
        }

        if (!selectedMatch) {
            const output = this.rememberUnmatched(identity, source, unmatchedRecord);
            return renderUnmatched ? output : undefined;
        }

        const records = selectedMatch.records;
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

    private hasCorrelatedIdentityTwin(identity: LogRecordIdentity, source: LogSource): boolean {
        return this.correlatedRecordCollectionsFor(identity).some(records =>
            records.some(candidate =>
                !candidate.sources.has(source)
                && matchRecordIdentities(candidate.identity, identity) !== undefined));
    }

    private correlatedRecordCollectionsFor(identity: LogRecordIdentity): CorrelatedRecord[][] {
        const collections: CorrelatedRecord[][] = [];
        const addCollection = (records: CorrelatedRecord[]): void => {
            if (!collections.includes(records)) {
                collections.push(records);
            }
        };

        for (const record of getIdentityRecords(identity)) {
            addCollection(this.correlatedRecordsFor(record));
        }
        // An ambiguous DebugLogger identity is stored only with its primary level. A later
        // provider must also find it when its retained delimiter interpretation uses the other pool.
        addCollection(this._correlatedRecords);
        addCollection(this._lowLevelCorrelatedRecords);
        return collections;
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

    private emitRawConsoleLoggerOutput(
        output: string,
        category: string,
        logLevel: AppHostLogLevel,
        outputs: AppHostParentOutput[]): void {
        outputs.push({
            output,
            category: category === 'stderr' || logLevel === 'Error' || logLevel === 'Critical'
                ? 'stderr'
                : 'stdout'
        });
    }

    private fallbackFilterFor(category: string): AppHostParentOutputFilter {
        let filter = this._fallbackFilters.get(category);
        if (!filter) {
            filter = new AppHostParentOutputFilter();
            this._fallbackFilters.set(category, filter);
        }

        return filter;
    }

    private static createPendingSingleLineRecord(
        line: string,
        category: string): PendingConsoleRecord | undefined {
        const records = parseSingleLineConsoleLoggerRecords(line);
        const record = records[0];
        if (!record) {
            return undefined;
        }

        const alternativeRecords: LogRecord[] = [];
        const addAlternative = (alternative: LogRecord): void => {
            if (alternativeRecords.length === maxSingleLineRecordAlternatives) {
                alternativeRecords.splice(1, 1);
            }
            alternativeRecords.push(alternative);
        };
        for (const alternative of records.slice(1)) {
            for (const offset of AppHostLogOutputCoordinator.getSingleLineScopeBodyOffsets(alternative.body)) {
                addAlternative({ ...alternative, body: alternative.body.slice(offset) });
            }
            // Keep the exact recent interpretation after its scope aliases. It is the only
            // lossless choice when a literal message itself begins with scope-shaped text.
            addAlternative(alternative);
        }

        return {
            record: {
                categoryName: record.categoryName,
                logLevel: record.logLevel,
                eventId: record.eventId,
                singleLine: true
            },
            body: record.body,
            alternativeRecords,
            leadingScopeBodyOffsets:
                AppHostLogOutputCoordinator.getSingleLineScopeBodyOffsets(record.body),
            raw: line,
            category,
            allowsContinuation: true,
            hasBodyLine: true,
            hasNonScopeBodyLine: true,
            overflowed: false,
            hasCrLf: line.includes('\r\n'),
            endsWithBareLf: line.endsWith('\n') && !line.endsWith('\r\n')
        };
    }

    private static getSingleLineScopeBodyOffsets(body: string): number[] {
        if (body === '=>') {
            return [body.length];
        }

        // Plain `=> scope message` is indistinguishable from a literal message. Retain
        // alternatives only while leading scope tokens have a key/value shape.
        const offsets: number[] = [];
        let bodyOffset = 0;
        while (bodyOffset < body.length) {
            const scopePrefix = /^=> \S+[:=]\S*(?: |$)/.exec(body.slice(bodyOffset))?.[0];
            if (!scopePrefix) {
                break;
            }

            bodyOffset += scopePrefix.length;
            if (offsets.length === AppHostLogOutputCoordinator._maxLeadingScopeBodyOffsets) {
                offsets.splice(1, 1);
            }
            offsets.push(bodyOffset);
        }

        return offsets;
    }

    private resetFallbackFilter(category: string): void {
        this._fallbackFilters.get(category)?.reset();
    }

    private scheduleIdleFlush(category: string): void {
        const pending = this._pendingRecords.get(category);
        const hasPendingDebugRecord = this._pendingDebugRecords.has(category);
        const hasPendingHeaderFragment = this._pendingConsoleHeaderFragments.has(category);
        if (!this._onIdleFlush
            || (!pending && !hasPendingDebugRecord && !hasPendingHeaderFragment && !this._partialLines.has(category))) {
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

            this.flushPendingConsoleHeaderFragment(category, outputs);
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
    const alternativeRecords = pending.alternativeRecords.map(alternative => ({
        ...alternative,
        body: normalizeRecordText(alternative.body)
    }));

    return {
        record,
        ...(alternativeRecords.length > 0 ? { alternativeRecords } : {}),
        ...(leadingScopeBodyOffsets.length > 0 ? { leadingScopeBodyOffsets } : {})
    };
}

const consoleLoggerTimestamp =
    String.raw`(?:\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)`;
const consoleLoggerTimestampPrefix =
    String.raw`(?:(?:${consoleLoggerTimestamp}|\[${consoleLoggerTimestamp}\])\s+)?`;
const consoleLoggerAnsiSgrSequence = String.raw`\x1b\[[0-9;]*m`;
const consoleLoggerLevelPattern =
    String.raw`(?:${consoleLoggerAnsiSgrSequence})*(trce|dbug|info|warn|fail|crit)(?:${consoleLoggerAnsiSgrSequence})*`;
const multilineConsoleLoggerHeaderRegex = new RegExp(
    String.raw`^${consoleLoggerTimestampPrefix}${consoleLoggerLevelPattern}: ([\s\S]*)\[(-?\d+)\](?:\r\n|\r|\n)$`);
const singleLineConsoleLoggerPrefixRegex = new RegExp(
    String.raw`^${consoleLoggerTimestampPrefix}${consoleLoggerLevelPattern}: ([\s\S]*?)(?:\r\n|\r|\n)?$`);
const debugLoggerLevelDelimiterRegex = /: (Trace|Debug|Information|Warning|Error|Critical): /g;

function getFirstLine(value: string, startOffset = 0): string {
    const lineBreakRegex = /\r\n|\r|\n/g;
    lineBreakRegex.lastIndex = startOffset;
    const lineBreak = lineBreakRegex.exec(value);
    return value.slice(startOffset, lineBreak ? lineBreak.index + lineBreak[0].length : value.length);
}

function getLineStartOffsets(value: string): number[] {
    const offsets: number[] = [];
    for (const match of value.matchAll(/\r\n|\r|\n/g)) {
        const offset = match.index + match[0].length;
        if (offset < value.length) {
            offsets.push(offset);
        }
    }
    return offsets;
}

function getAmbiguousDebugLineBoundaries(raw: string): { rawOffset: number }[] {
    const lineStartOffsets = getLineStartOffsets(raw);
    const pending = createPendingDebugRecord(raw.slice(0, lineStartOffsets[0] ?? raw.length), '');
    const boundaries: { rawOffset: number }[] = [];

    for (let index = 0; index < lineStartOffsets.length; index++) {
        const rawOffset = lineStartOffsets[index];
        const line = raw.slice(rawOffset, lineStartOffsets[index + 1] ?? raw.length);
        if (isDebugLoggerHeader(line) || !isDebugLoggerContinuation(pending, line)) {
            boundaries.push({ rawOffset });
        }
        appendPendingDebugLine(pending, line);
    }

    return boundaries;
}

function createPendingDebugRecord(line: string, category: string): PendingDebugRecord {
    return {
        raw: line,
        category,
        hasException: false,
        ambiguousLineBoundaries: []
    };
}

function getTrailingBodyEndOffsets(
    raw: string,
    boundaries: readonly { rawOffset: number }[],
    recordBodyLength: number): number[] {
    return [...new Set(
        boundaries
            .map(boundary => parseDebugLoggerRecord(raw.slice(0, boundary.rawOffset))?.body.length)
            .filter((offset): offset is number =>
                offset !== undefined && offset >= 0 && offset < recordBodyLength))];
}

function addDebugLoggerTrailingAliases(
    identity: LogRecordIdentity,
    raw: string,
    boundaries: readonly { rawOffset: number }[]): LogRecordIdentity {
    const trailingBodyEndOffsets = [...new Set([
        ...(identity.trailingBodyEndOffsets ?? []),
        ...getTrailingBodyEndOffsets(raw, boundaries, identity.record.body.length)
    ])];
    const trailingBoundaryRecords = [...(identity.trailingBoundaryRecords ?? [])]
        .slice(0, maxDebugLoggerRecordAlternatives);

    if (identity.alternativeRecords?.length
        && trailingBoundaryRecords.length < maxDebugLoggerRecordAlternatives) {
        const seenRawOffsets = new Set<number>();
        // Boundaries and delimiter interpretations can both reach 128. Parse boundary-first
        // so every retained delimiter gets the common first boundary without retaining their
        // cross product.
        for (const boundary of boundaries) {
            if (seenRawOffsets.has(boundary.rawOffset)) {
                continue;
            }
            seenRawOffsets.add(boundary.rawOffset);

            const boundaryIdentity = createDebugLoggerIdentity(raw.slice(0, boundary.rawOffset));
            for (const record of boundaryIdentity?.alternativeRecords ?? []) {
                trailingBoundaryRecords.push(record);
                if (trailingBoundaryRecords.length >= maxDebugLoggerRecordAlternatives) {
                    break;
                }
            }
            if (trailingBoundaryRecords.length >= maxDebugLoggerRecordAlternatives) {
                break;
            }
        }
    }

    return {
        ...identity,
        ...(trailingBodyEndOffsets.length > 0 ? { trailingBodyEndOffsets } : {}),
        ...(trailingBoundaryRecords.length > 0 ? { trailingBoundaryRecords } : {})
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
    return parseSingleLineConsoleLoggerRecords(line)[0];
}

function parseSingleLineConsoleLoggerRecords(line: string): LogRecord[] {
    // With SimpleConsoleFormatterOptions.SingleLine, the same record is:
    //   warn: Example.Category[7] First message line.
    // Category text and message text are both arbitrary, so retain every possible event-ID
    // split and let another provider identify the actual record.
    const match = singleLineConsoleLoggerPrefixRegex.exec(line);
    if (!match) {
        return [];
    }

    const records: LogRecord[] = [];
    for (const eventIdMatch of match[2].matchAll(/\[(-?\d+)\] /g)) {
        const record = {
            categoryName: escapeCategoryControlCharacters(match[2].slice(0, eventIdMatch.index)),
            logLevel: getFullLoggerLevel(match[1]),
            eventId: Number(eventIdMatch[1]),
            body: normalizeRecordText(match[2].slice(eventIdMatch.index + eventIdMatch[0].length)),
            singleLine: true
        };
        if (records.length === maxSingleLineRecordAlternatives + 1) {
            records.splice(2, 1);
        }
        records.push(record);
    }

    return records;
}

function parseDebugLoggerRecord(output: string): LogRecord | undefined {
    return parseDebugLoggerRecords(output)[0];
}

function parseDebugLoggerRecords(output: string): LogRecord[] {
    // DebugLogger writes:
    //   Example.Category: Warning: Deployment failed.
    //
    //   System.InvalidOperationException: boom
    // Category and message text are arbitrary, so each ": Warning: "-shaped delimiter can
    // identify the record until another provider confirms the actual category and body.
    // It doesn't include the event ID, so correlation treats a missing ID as a wildcard
    // while still requiring category, level, and the complete normalized body to match.
    const normalized = normalizeRecordText(output.replace(/(?:\r\n|\r|\n)$/, ''));
    const candidates: { delimiterIndex: number; delimiterLength: number; logLevel: AppHostLogLevel }[] = [];
    let categoryHasPeriod = false;
    let categoryHasWhitespace = false;
    let scannedCategoryEnd = 0;
    for (const match of normalized.matchAll(debugLoggerLevelDelimiterRegex)) {
        while (scannedCategoryEnd < match.index) {
            const character = normalized[scannedCategoryEnd++];
            categoryHasPeriod ||= character === '.';
            categoryHasWhitespace ||= /\s/.test(character);
        }

        const logLevel = match[1] as AppHostLogLevel;
        if (!isSupportedDebugLoggerCategoryPrefix(
            normalized[0] ?? '',
            categoryHasPeriod,
            categoryHasWhitespace,
            logLevel)) {
            continue;
        }

        if (candidates.length === maxDebugLoggerRecordAlternatives + 1) {
            candidates.splice(1, 1);
        }
        candidates.push({
            delimiterIndex: match.index,
            delimiterLength: match[0].length,
            logLevel
        });
    }

    return candidates.map(candidate => {
        const body = normalized.slice(candidate.delimiterIndex + candidate.delimiterLength);
        const { message, exception } = splitMessageAndException(body);
        return {
            categoryName: escapeCategoryControlCharacters(
                normalized.slice(0, candidate.delimiterIndex)),
            logLevel: candidate.logLevel,
            eventId: undefined,
            body: normalizeRecordText(joinRecordBody(message, exception))
        };
    });
}

function createDebugLoggerIdentity(output: string): LogRecordIdentity | undefined {
    const records = parseDebugLoggerRecords(output);
    const record = records[0];
    return record
        ? {
            record,
            ...(records.length > 1 ? { alternativeRecords: records.slice(1) } : {})
        }
        : undefined;
}

function isDebugLoggerHeader(line: string): boolean {
    const content = line.replace(/(?:\r\n|\r|\n)$/, '');
    let categoryHasPeriod = false;
    let categoryHasWhitespace = false;
    let scannedCategoryEnd = 0;
    for (const match of content.matchAll(debugLoggerLevelDelimiterRegex)) {
        while (scannedCategoryEnd < match.index) {
            const character = content[scannedCategoryEnd++];
            categoryHasPeriod ||= character === '.';
            categoryHasWhitespace ||= /\s/.test(character);
        }

        if (isSupportedDebugLoggerCategoryPrefix(
            content[0] ?? '',
            categoryHasPeriod,
            categoryHasWhitespace,
            match[1] as AppHostLogLevel)) {
            return true;
        }
    }

    return false;
}

function isSupportedDebugLoggerCategoryPrefix(
    firstCharacter: string,
    hasPeriod: boolean,
    hasWhitespace: boolean,
    logLevel: AppHostLogLevel): boolean {
    return hasPeriod
        || (logLevel === 'Trace' || logLevel === 'Debug')
            && (!hasWhitespace || /^[A-Z_]/.test(firstCharacter));
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
    let exceptionIndex = -1;
    let hasClrExceptionStructure = false;
    for (let index = lines.length - 1; index > 0; index--) {
        const line = lines[index];
        if (lines[index - 1] === ''
            && (isConventionalDebugLoggerExceptionStart(line)
                || isNamespacedClrTypeStart(line) && hasClrExceptionStructure)) {
            exceptionIndex = index;
            break;
        }
        hasClrExceptionStructure ||= isClrExceptionStructureLine(line);
    }
    if (exceptionIndex < 0) {
        return { message: value };
    }

    return {
        message: lines.slice(0, exceptionIndex - 1).join('\n'),
        exception: lines.slice(exceptionIndex).join('\n')
    };
}

function isDebugLoggerExceptionStart(line: string): boolean {
    return isConventionalDebugLoggerExceptionStart(line);
}

function isConventionalDebugLoggerExceptionStart(line: string): boolean {
    return /^(?:(?:[A-Za-z_][\w`]*\.)*[\w`]*(?:Exception|Error)(?: \([^)]*\))?:|Unhandled exception\.)/.test(line);
}

function isNamespacedClrTypeStart(line: string): boolean {
    return /^(?:[A-Za-z_][\w`]*\.)+[A-Za-z_][\w`]*(?:\+[A-Za-z_][\w`]*)*(?:\[[^\r\n]+\])?(?: \([^)]*\))?:/.test(line);
}

function isClrExceptionStructureLine(line: string): boolean {
    return /^\s+at (?:[^\s.()]+\.)+[^\s.()]+\([^)]*\)(?: in .*:line \d+)?$/.test(line)
        || /^\s*---> /.test(line)
        || /^--- End of /.test(line);
}

function isConsoleLoggerContinuation(line: string): boolean {
    const content = line.replace(/(?:\r\n|\r|\n)$/, '');
    return content.startsWith('      ');
}

function isWindowsBareLfContinuation(pending: PendingConsoleRecord, line: string): boolean {
    // On Windows SimpleConsoleFormatter only indents Environment.NewLine (`\r\n`).
    // A bare LF embedded in the message therefore leaves the following line unindented.
    return pending.endsWithBareLf
        && (pending.record.singleLine === true
            ? !parseSingleLineConsoleLoggerRecord(line)
                && !parseMultilineConsoleLoggerHeader(line)
            : pending.hasCrLf);
}

function updatePendingConsoleLineEnding(pending: PendingConsoleRecord, line: string): void {
    pending.hasCrLf ||= line.includes('\r\n');
    pending.endsWithBareLf = line.endsWith('\n') && !line.endsWith('\r\n');
}

function hasUnconfirmedSingleLineContinuations(pending: PendingConsoleRecord): boolean {
    return pending.record.singleLine === true
        && !pending.raw.includes('\r\n')
        && getFirstLine(pending.raw).length < pending.raw.length;
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
    for (const leftRecord of getIdentityRecords(left)) {
        for (const rightRecord of getIdentityRecords(right)) {
            if (recordHeadersMatch(leftRecord, rightRecord)
                && recordBodiesMatchAt(leftRecord, 0, rightRecord)) {
                return {
                    record: createCanonicalRecord(leftRecord, rightRecord),
                    isExactBody: true
                };
            }
        }
    }

    if (!recordHeadersMatch(left.record, right.record)) {
        return undefined;
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

function getIdentityRecords(identity: LogRecordIdentity): readonly LogRecord[] {
    return [
        identity.record,
        ...(identity.alternativeRecords ?? []),
        ...(identity.trailingBoundaryRecords ?? [])
    ];
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
