import { AnsiColors, applyTextStyle } from '../utils/strings';

export type AppHostLogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';

export interface AppHostLogEntry {
    sequenceNumber: number;
    timestamp: string;
    logLevel: AppHostLogLevel;
    message: string;
    categoryName: string;
    eventId: number;
    eventName?: string | null;
    exception?: string | null;
}

export interface AppHostParentOutput {
    output: string;
    category: 'stdout' | 'stderr';
}

type AppHostLogSource = 'backchannel' | 'consoleLogger' | 'debugLogger';

interface AppHostLoggerRecord {
    categoryName: string;
    logLevel: AppHostLogLevel;
    message: string;
    eventId?: number;
    exception?: string;
}

interface CorrelatedRecord {
    record: AppHostLoggerRecord;
    sources: Set<AppHostLogSource>;
}

interface PendingConsoleRecord {
    header: string;
    body: string;
    category: string;
}

export class AppHostLogOutputCoordinator {
    // Correlation is one-for-one, so repeated identical ILogger calls remain distinct.
    // The queue only needs to bridge provider/RPC interleaving and is owned by one
    // Aspire debug session; older records are never used for broad message suppression.
    private static readonly _maxCorrelatedRecords = 1024;
    private static readonly _allSources: readonly AppHostLogSource[] = ['backchannel', 'consoleLogger', 'debugLogger'];
    private readonly _correlatedRecords: CorrelatedRecord[] = [];
    private readonly _fallbackFilter = new AppHostParentOutputFilter();
    private readonly _partialLines = new Map<string, string>();
    private _highestBackchannelSequence = 0;
    // `stdout`, `stderr` and `console` are independent streams that interleave freely, so a
    // record being assembled on one of them says nothing about the others. A single pending
    // record would let an unrelated write on another stream terminate a record mid-assembly,
    // rendering the truncated half and leaking the rest.
    private readonly _pendingRecords = new Map<string, PendingConsoleRecord>();

    handleBackchannelEntry(entry: AppHostLogEntry): AppHostParentOutput | undefined {
        if (entry.sequenceNumber > 0) {
            // A transient CLI/AppHost backchannel reconnect re-subscribes to the
            // provider's replay buffer. Sequence numbers are monotonic for one
            // AppHost process, so a lower/equal value is the same record, not a
            // repeated message that should be shown again.
            if (entry.sequenceNumber <= this._highestBackchannelSequence) {
                return undefined;
            }

            this._highestBackchannelSequence = entry.sequenceNumber;
        }

        return this.correlate({
            categoryName: entry.categoryName,
            logLevel: entry.logLevel,
            message: normalizeRecordText(entry.message),
            eventId: entry.eventId,
            exception: normalizeOptionalRecordText(entry.exception)
        }, 'backchannel');
    }

    /**
     * Consumes one debug adapter output event and returns whatever became renderable.
     *
     * A record is only known to have ended once a line that cannot continue it arrives, so
     * the last record of a burst stays buffered until the next event or {@link flush}.
     * That costs no visible latency: the CLI relays the same record over its own path —
     * structured for a capable extension, a dim message for an older one — and whichever
     * copy lands first is the one rendered. Guessing that a record had ended instead would
     * render it truncated and leak the rest as raw text whenever a stream chunk happened
     * to break on a line boundary inside the record.
     */
    handleDebugAdapterOutput(output: string, category: string | undefined): AppHostParentOutput[] {
        const normalizedCategory = category ?? 'console';
        const outputs: AppHostParentOutput[] = [];

        const buffered = `${this._partialLines.get(normalizedCategory) ?? ''}${output}`;
        const lastBreak = Math.max(buffered.lastIndexOf('\n'), buffered.lastIndexOf('\r'));
        const completeLines = buffered.slice(0, lastBreak + 1);
        let partial = buffered.slice(lastBreak + 1);

        if (completeLines.length > 0) {
            this.consumeLines(completeLines, normalizedCategory, outputs);
        }

        // Decide about the trailing partial line only after the complete lines have been
        // consumed, because whether a record is being assembled is exactly what makes an
        // unterminated line worth waiting for.
        if (partial.length > 0 && !this.shouldHoldPartialLine(partial, normalizedCategory)) {
            this.consumeLines(partial, normalizedCategory, outputs);
            partial = '';
        }

        if (partial.length > 0) {
            this._partialLines.set(normalizedCategory, partial);
        } else {
            this._partialLines.delete(normalizedCategory);
        }

        return outputs;
    }

    /**
     * Emits whatever is still being assembled, without discarding correlation state.
     *
     * Records are assembled across output events, so an AppHost that exits right after
     * logging would otherwise take the final record with it — exactly the `fail:`/`crit:`
     * line the user needs to see. Correlation state is kept so a backchannel copy still
     * in flight is recognized as a duplicate rather than rendered again.
     */
    flush(): AppHostParentOutput[] {
        const outputs: AppHostParentOutput[] = [];
        const partials = [...this._partialLines.entries()];
        this._partialLines.clear();

        for (const [category, partial] of partials) {
            this.consumeLines(partial, category, outputs);
        }

        for (const category of [...this._pendingRecords.keys()]) {
            this.flushPendingRecord(category, outputs);
        }

        return outputs;
    }

    reset(): void {
        this._correlatedRecords.length = 0;
        this._highestBackchannelSequence = 0;
        this._pendingRecords.clear();
        this._partialLines.clear();
        this._fallbackFilter.reset();
    }

    /**
     * Decides whether an unterminated trailing line is worth waiting on.
     *
     * Debug adapter output is not aligned to record boundaries: a redirected
     * `Console.Out` flushes every 256 characters, so a long record reaches the adapter in
     * several writes and parsing a chunk that stops mid-line would render a truncated
     * record and leak the remainder as raw text. Holding text also delays it until the
     * next write, so only hold when it plausibly belongs to a logger record —
     * unstructured writes such as a progress indicator printed without a newline still
     * reach the console immediately.
     */
    private shouldHoldPartialLine(partial: string, category: string): boolean {
        // `console` carries DebugLogger and adapter output only, never interactive
        // writes, so nothing observable is delayed by buffering it.
        if (category === 'console') {
            return true;
        }

        if (this._pendingRecords.has(category)) {
            return true;
        }

        return couldStartConsoleLoggerHeader(partial);
    }

    private consumeLines(text: string, category: string, outputs: AppHostParentOutput[]): void {
        const lines = text.match(/[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+/g) ?? [];
        let passthrough = '';

        const flushPassthrough = () => {
            if (passthrough.length === 0) {
                return;
            }

            const block = passthrough;
            passthrough = '';

            // System.Diagnostics.Debug output is delivered as DAP `console` output,
            // while Console.WriteLine uses stdout/stderr. Restrict the DebugLogger
            // grammar to that provenance so user stdout shaped like
            // "Status: Error: connection refused" is never reclassified.
            const record = category === 'console' ? parseDebugLoggerRecord(block) : undefined;
            if (record) {
                this.emitRecord(record, 'debugLogger', block, category, outputs);
                return;
            }

            const filtered = this._fallbackFilter.filter(block, category);
            if (filtered) {
                outputs.push(filtered);
            }
        };

        for (const line of lines) {
            const pending = this._pendingRecords.get(category);
            if (pending && isConsoleLoggerContinuation(line)) {
                pending.body += line;
                continue;
            }

            this.flushPendingRecord(category, outputs);

            if (isConsoleLoggerHeader(line)) {
                flushPassthrough();
                this._pendingRecords.set(category, { header: line, body: '', category });
                continue;
            }

            passthrough += line;
        }

        flushPassthrough();
    }

    private flushPendingRecord(category: string, outputs: AppHostParentOutput[]): void {
        const pending = this._pendingRecords.get(category);
        if (!pending) {
            return;
        }

        this._pendingRecords.delete(category);
        const text = `${pending.header}${pending.body}`;

        const record = parseConsoleLoggerRecord(text);
        if (record) {
            this.emitRecord(record, 'consoleLogger', text, pending.category, outputs);
            return;
        }

        const filtered = this._fallbackFilter.filter(text, pending.category);
        if (filtered) {
            outputs.push(filtered);
        }
    }

    private emitRecord(
        record: AppHostLoggerRecord,
        source: AppHostLogSource,
        rawText: string,
        category: string,
        outputs: AppHostParentOutput[]): void {
        // Advance the fallback filter even though its output is discarded. It tracks
        // whether the previous line opened a suppressed trace/debug record or an error
        // block, so skipping consumed records leaves that state stale and the next
        // unstructured line is classified against the wrong record.
        this._fallbackFilter.filter(rawText, category);

        const correlated = this.correlate(record, source);
        if (correlated) {
            outputs.push(correlated);
        }
    }

    private correlate(record: AppHostLoggerRecord, source: AppHostLogSource): AppHostParentOutput | undefined {
        const existingIndex = this._correlatedRecords.findIndex(candidate =>
            !candidate.sources.has(source) && areEquivalentRecords(candidate.record, record));

        if (existingIndex >= 0) {
            const existing = this._correlatedRecords[existingIndex];
            existing.sources.add(source);

            // Once every provenance has been seen the record can never match again, so
            // drop it immediately. Otherwise the window fills with dead entries and
            // evicts records that are still waiting for their twin.
            if (existing.sources.size === AppHostLogOutputCoordinator._allSources.length) {
                this._correlatedRecords.splice(existingIndex, 1);
            }

            return undefined;
        }

        this._correlatedRecords.push({
            record,
            sources: new Set([source])
        });
        if (this._correlatedRecords.length > AppHostLogOutputCoordinator._maxCorrelatedRecords) {
            this._correlatedRecords.shift();
        }

        return formatLoggerRecord(record);
    }
}

export class AppHostParentOutputFilter {
    private _continuingDroppedLog = false;
    private _continuingErrorBlock = false;
    private _lastCategory: string | undefined;

    filter(output: string, category: string | undefined): AppHostParentOutput | undefined {
        // Per the DAP spec the `category` field is optional; clients should treat a
        // missing category as `'console'`. Normalize once at the boundary so state
        // tracking and per-line classification see a consistent value.
        const normalizedCategory = category ?? 'console';

        if (normalizedCategory === 'debug') {
            this.resetLineState();
            this._lastCategory = normalizedCategory;
            return undefined;
        }

        if (normalizedCategory !== this._lastCategory) {
            this.resetLineState();
        }
        this._lastCategory = normalizedCategory;

        const segments = output.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g)?.filter(segment => segment.length > 0) ?? [];
        let filteredOutput = '';
        let hasErrorOutput = normalizedCategory === 'stderr';

        for (const segment of segments) {
            const outputCategory = this.getLineCategory(segment, normalizedCategory);
            if (outputCategory) {
                filteredOutput += segment;
                hasErrorOutput ||= outputCategory === 'stderr';
            }
        }

        if (filteredOutput.length === 0) {
            return undefined;
        }

        return {
            output: filteredOutput,
            category: hasErrorOutput ? 'stderr' : 'stdout'
        };
    }

    reset(): void {
        this.resetLineState();
        this._lastCategory = undefined;
    }

    private getLineCategory(segment: string, category: string): 'stdout' | 'stderr' | undefined {
        const line = segment.replace(/(?:\r\n|\r|\n)$/, '');
        const trimmedLine = line.trim();

        if (trimmedLine.length === 0) {
            return !this._continuingDroppedLog && this.shouldMirrorConsoleOutput(category) ? this.getCurrentCategory(category) : undefined;
        }

        if (this._continuingDroppedLog && isIndentedContinuation(line)) {
            return undefined;
        }

        if (this._continuingErrorBlock && isIndentedContinuation(line)) {
            return 'stderr';
        }

        const logSeverity = getConsoleLogSeverity(trimmedLine);
        if (logSeverity) {
            this._continuingDroppedLog = logSeverity === 'low';
            this._continuingErrorBlock = logSeverity === 'severe';

            return logSeverity === 'low' ? undefined : this.getCurrentCategory(category);
        }

        const isSevereOutput = isSevereRuntimeOutputLine(trimmedLine);
        this._continuingDroppedLog = false;
        this._continuingErrorBlock = isSevereOutput;

        if (category === 'console' && !isSevereOutput) {
            return undefined;
        }

        return this.getCurrentCategory(category);
    }

    private shouldMirrorConsoleOutput(category: string): boolean {
        return category !== 'console' || this._continuingErrorBlock;
    }

    private getCurrentCategory(category: string): 'stdout' | 'stderr' {
        return category === 'stderr' || this._continuingErrorBlock ? 'stderr' : 'stdout';
    }

    private resetLineState(): void {
        this._continuingDroppedLog = false;
        this._continuingErrorBlock = false;
    }
}

function parseConsoleLoggerRecord(output: string): AppHostLoggerRecord | undefined {
    // SimpleConsoleFormatter emits one logical record as:
    //   warn: Example.Category[7]
    //         First message line.
    //         System.InvalidOperationException: boom
    // CoreCLR can split the header and indented body into separate DAP events;
    // AppHostLogOutputCoordinator joins that exact two-event shape before parsing.
    const normalized = normalizeLineEndings(output);
    const match = /^(trce|dbug|info|warn|fail|crit): (.+)\[(-?\d+)\]\n([\s\S]+)$/.exec(normalized);
    if (!match) {
        return undefined;
    }

    const bodyLines = removeSingleTrailingNewline(match[4]).split('\n');
    if (bodyLines.some(line => line.length > 0 && !line.startsWith('      '))) {
        return undefined;
    }

    const body = bodyLines.map(line => line.startsWith('      ') ? line.slice(6) : line).join('\n');
    const { message, exception } = splitMessageAndException(body);

    return {
        categoryName: match[2],
        logLevel: getFullLoggerLevel(match[1]),
        message: normalizeRecordText(message),
        eventId: Number(match[3]),
        exception: normalizeOptionalRecordText(exception)
    };
}

function isConsoleLoggerHeader(output: string): boolean {
    return /^(trce|dbug|info|warn|fail|crit): .+\[-?\d+\](?:\r\n|\r|\n)?$/.test(output);
}

const consoleLoggerLevelTokens = ['trce', 'dbug', 'info', 'warn', 'fail', 'crit'];

function couldStartConsoleLoggerHeader(text: string): boolean {
    // Compare against the full `level: ` prefix in both directions so every intermediate
    // state matches, including the one where the level is complete but the separator is
    // still arriving ("fail" -> "fail:" -> "fail: ").
    return consoleLoggerLevelTokens.some(token => {
        const prefix = `${token}: `;
        return prefix.startsWith(text) || text.startsWith(prefix);
    });
}

function isConsoleLoggerContinuation(output: string): boolean {
    const lines = normalizeLineEndings(output).split('\n');
    if (lines.at(-1) === '') {
        lines.pop();
    }

    return lines.length > 0 && lines.every(line => line.length === 0 || line.startsWith('      '));
}

function parseDebugLoggerRecord(output: string): AppHostLoggerRecord | undefined {
    // DebugLogger emits a complete record as:
    //   Example.Category: Warning: First message line.
    //
    //   System.InvalidOperationException: boom
    const normalized = removeSingleTrailingNewline(normalizeLineEndings(output));
    const match = /^([^\n]+): (Trace|Debug|Information|Warning|Error|Critical): ([\s\S]*)$/.exec(normalized);
    if (!match) {
        return undefined;
    }

    const { message, exception } = splitMessageAndException(match[3]);
    return {
        categoryName: match[1],
        logLevel: match[2] as AppHostLogLevel,
        message: normalizeRecordText(message),
        exception: normalizeOptionalRecordText(exception)
    };
}

function splitMessageAndException(value: string): { message: string; exception?: string } {
    // Exception.ToString() starts with the type name followed by ": " and the message,
    // except for the Win32Exception family, which inserts the native error code:
    //   System.InvalidOperationException: boom
    //   System.Net.Sockets.SocketException (111): Connection refused
    //   System.ComponentModel.Win32Exception (2): No such file or directory
    // https://learn.microsoft.com/dotnet/api/system.componentmodel.win32exception.tostring
    const lines = normalizeLineEndings(value).split('\n');
    const exceptionIndex = lines.findIndex(line =>
        /^(?:[A-Za-z_][\w`]*(?:\.[A-Za-z_][\w`]*)*(?:Exception|Error)(?: \([^)]*\))?:|Unhandled exception\.)/.test(line));

    if (exceptionIndex < 0) {
        return { message: value };
    }

    const messageLines = lines.slice(0, exceptionIndex);
    if (messageLines.at(-1) === '') {
        messageLines.pop();
    }

    return {
        message: messageLines.join('\n'),
        exception: lines.slice(exceptionIndex).join('\n')
    };
}

function areEquivalentRecords(left: AppHostLoggerRecord, right: AppHostLoggerRecord): boolean {
    if (left.categoryName !== right.categoryName || left.logLevel !== right.logLevel) {
        return false;
    }

    // DebugLogger omits the event id entirely, so an absent value on either side is a
    // wildcard rather than a mismatch.
    if (left.eventId !== undefined && right.eventId !== undefined && left.eventId !== right.eventId) {
        return false;
    }

    if (left.exception !== undefined && right.exception !== undefined) {
        return left.message === right.message && left.exception === right.exception;
    }

    // Only one side separated an exception from the message, which happens in two ways:
    //
    //   1. The AppHost predates BackchannelLogEntry.Exception. Its structured message is
    //      `formatter(state, exception)`, which drops the exception, while the console
    //      copy still prints it. The formatted messages match.
    //   2. A multi-line message whose continuation happens to look like an exception,
    //      e.g. "Retry failed.\nSystem.TimeoutException: timed out" passed as a single
    //      message. The console copy splits it; the structured copy does not. The
    //      recombined bodies match.
    //
    // Accepting either keeps both cases correlated instead of rendering them twice.
    return left.message === right.message || getRecordBody(left) === getRecordBody(right);
}

function getRecordBody(record: AppHostLoggerRecord): string {
    return record.exception ? `${record.message}\n${record.exception}` : record.message;
}

// Standard SGR codes, resolved through the workbench ANSI palette so the rendered
// record follows the active color theme.
function formatLoggerRecord(record: AppHostLoggerRecord): AppHostParentOutput {
    const body = `${record.categoryName}: ${record.logLevel}: ${record.message}${record.exception ? `\n${record.exception}` : ''}`;
    const category = record.logLevel === 'Error' || record.logLevel === 'Critical' ? 'stderr' : 'stdout';
    const style = record.logLevel === 'Trace' || record.logLevel === 'Debug'
        ? AnsiColors.Dim
        : record.logLevel === 'Warning'
            ? AnsiColors.Yellow
            : undefined;

    return {
        // Every rendered record carries its own terminator. The debug console appends
        // output verbatim, so an unterminated record would run into the next one. The
        // ANSI reset stays inside the line so the newline is not styled.
        output: `${applyTextStyle(body, style)}\n`,
        category
    };
}

function normalizeOptionalRecordText(value: string | null | undefined): string | undefined {
    return value ? normalizeRecordText(value) : undefined;
}

function normalizeRecordText(value: string): string {
    // Trailing padding differs per source. SimpleConsoleFormatter indents every
    // continuation line, so `logger.LogInformation("text\n")` arrives on the console as
    // an extra padded line while the backchannel copy keeps a bare newline. Trimming
    // trailing whitespace makes the two comparable and does not change how a record
    // reads.
    return normalizeLineEndings(value).replace(/[ \t\n]+$/, '');
}

function normalizeLineEndings(value: string): string {
    return value.replace(/\r\n|\r/g, '\n');
}

function removeSingleTrailingNewline(value: string): string {
    return value.endsWith('\n') ? value.slice(0, -1) : value;
}

function getFullLoggerLevel(shortLevel: string): AppHostLogLevel {
    switch (shortLevel) {
        case 'trce':
            return 'Trace';
        case 'dbug':
            return 'Debug';
        case 'info':
            return 'Information';
        case 'warn':
            return 'Warning';
        case 'fail':
            return 'Error';
        case 'crit':
            return 'Critical';
        default:
            throw new Error(`Unknown logger level: ${shortLevel}`);
    }
}

function getConsoleLogSeverity(line: string): 'low' | 'normal' | 'severe' | undefined {
    const defaultConsoleLogLevel = /^(trce|dbug|info|warn|fail|crit):\s/.exec(line)?.[1];
    if (defaultConsoleLogLevel) {
        return defaultConsoleLogLevel === 'trce' || defaultConsoleLogLevel === 'dbug'
            ? 'low'
            : defaultConsoleLogLevel === 'fail' || defaultConsoleLogLevel === 'crit'
                ? 'severe'
                : 'normal';
    }

    // Real category names are namespaced .NET type names. Requiring a dot avoids
    // treating arbitrary stdout such as "Status: Error: connection refused" as a log.
    const simpleConsoleLogLevel = /^[A-Za-z_]\w*(?:\.\w+)+(?:\[[^\]]+\])?:\s*(Trace|Debug|Information|Warning|Error|Critical):\s/.exec(line)?.[1];
    if (simpleConsoleLogLevel) {
        return simpleConsoleLogLevel === 'Trace' || simpleConsoleLogLevel === 'Debug'
            ? 'low'
            : simpleConsoleLogLevel === 'Error' || simpleConsoleLogLevel === 'Critical'
                ? 'severe'
                : 'normal';
    }

    return undefined;
}

function isIndentedContinuation(line: string): boolean {
    return /^\s+\S/.test(line);
}

function isSevereRuntimeOutputLine(line: string): boolean {
    return /(?:^|\s)(?:[A-Za-z_][\w`]*\.)+(?:[A-Za-z_][\w`]*Exception|Exception):/.test(line)
        || /^(?:Uncaught\s+)?(?:[A-Za-z_$][\w$]*Error|Error)(?:\s+\[[^\]]+\])?:/.test(line)
        || /^(?:fatal|critical|panic|aborted|segmentation\s+fault|unhandled\s+exception)\b/i.test(line);
}
