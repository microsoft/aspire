import * as vscode from 'vscode';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { aspireCliCommandTimedOut, aspireCommandOutputTruncated } from '../loc/strings';
import { isNoLogoUnsupportedOutput, noLogoOption, removeRootNoLogoOption } from '../utils/cliCompatibility';
import { AspireCliFailedError, AspireCliNotInstalledError } from './appHostCliContracts';
import { normalizeResourceCommandStatusLine } from './resourceCommandStatusOutput';

export const oneShotOutputBufferLimit = 64 * 1024;

export interface RunCliCommandOptions {
    timeoutMs?: number | null;
    stdoutBufferLimit?: number | null;
    cancellationToken?: vscode.CancellationToken;
    env?: { name: string; value: string }[];
}

export class AppHostCliRunner implements vscode.Disposable {
    private static readonly _oneShotCommandTimeoutMs = 30000;

    private _noLogoSupported = true;
    private _oneShotProcesses = new Set<ChildProcessWithoutNullStreams>();

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    withNoLogo(args: string[]): string[] {
        if (!this._noLogoSupported) {
            return args;
        }

        const appHostIndex = args.indexOf('--apphost');
        const insertIndex = appHostIndex === -1 ? args.length : appHostIndex;
        return [...args.slice(0, insertIndex), noLogoOption, ...args.slice(insertIndex)];
    }

    // Returns the args to retry with when the installed CLI does not recognize --nologo, or
    // undefined when this failure is unrelated to --nologo. Has the intentional side effect of
    // flipping _noLogoSupported to false the first time the unsupported pattern is observed so
    // subsequent withNoLogo calls stop adding the option for the lifetime of the runner.
    //
    // Callers that own their own retry args use the returned value directly; long-lived watch
    // restarters (describe/ps follow) use disableNoLogoForRetry below and intentionally discard
    // the returned args because the watch starter rebuilds args via withNoLogo.
    tryGetNoLogoRetryArgs(args: string[], stdout: string, stderr: string, operation: string): string[] | undefined {
        if (!isNoLogoUnsupportedOutput(args, stdout, stderr)) {
            return undefined;
        }

        if (this._noLogoSupported) {
            this._noLogoSupported = false;
            extensionLogOutputChannel.info(`Installed Aspire CLI does not recognize ${noLogoOption}; retrying ${operation} without it.`);
        }

        return removeRootNoLogoOption(args);
    }

    // Boolean variant of tryGetNoLogoRetryArgs for watch restarters that rebuild args via
    // withNoLogo when they restart. These call sites only need to know "did we just disable
    // --nologo support for the rest of this session?" — the recomputed args from
    // tryGetNoLogoRetryArgs would be thrown away.
    disableNoLogoForRetry(args: string[], stdout: string, stderr: string, operation: string): boolean {
        return this.tryGetNoLogoRetryArgs(args, stdout, stderr, operation) !== undefined;
    }

    async runCliCommand(command: string, args: string[], options: RunCliCommandOptions = {}): Promise<{ stdout: string; stderr: string }> {
        const cliPath = await this._terminalProvider.getAspireCliExecutablePath().catch(error => {
            throw new AspireCliNotInstalledError(String(error));
        });

        if (options.cancellationToken?.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        return new Promise<{ stdout: string; stderr: string }>((resolve, reject) => {
            let settled = false;
            let timeoutTimer: ReturnType<typeof setTimeout> | undefined;
            let cliProcess: ChildProcessWithoutNullStreams | undefined;
            // spawnCliProcess can invoke the exit/error callback before it returns (a synchronous
            // spawn failure), which settles this command while `cliProcess` is still unassigned.
            // Remember that so the finished handle is never added to _oneShotProcesses below, where
            // it would be retained for the lifetime of the runner. Mirrors the synchronous-completion
            // guard in AppHostPsPoller.
            let settledBeforeTracking = false;
            let cancellationRegistration: vscode.Disposable | undefined;
            const timeoutMs = options.timeoutMs === undefined ? AppHostCliRunner._oneShotCommandTimeoutMs : options.timeoutMs;
            const stdoutBufferLimit = options.stdoutBufferLimit === undefined ? null : options.stdoutBufferLimit;
            const stdout = new LimitedOutputBuffer(stdoutBufferLimit);
            const stderr = new LimitedOutputBuffer(oneShotOutputBufferLimit);

            const settle = (callback: () => void) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeoutTimer) {
                    clearTimeout(timeoutTimer);
                    timeoutTimer = undefined;
                }
                cancellationRegistration?.dispose();
                cancellationRegistration = undefined;
                if (cliProcess) {
                    this._oneShotProcesses.delete(cliProcess);
                    if (cliProcess.exitCode === null && !cliProcess.killed) {
                        terminateCliProcess(cliProcess, command);
                    }
                } else {
                    settledBeforeTracking = true;
                }
                callback();
            };

            if (timeoutMs !== null) {
                timeoutTimer = setTimeout(() => {
                    settle(() => reject(new AspireCliFailedError(command, null, stdout.value, stderr.value || aspireCliCommandTimedOut(timeoutMs))));
                }, timeoutMs);
            }

            cancellationRegistration = options.cancellationToken?.onCancellationRequested(() => {
                settle(() => reject(new vscode.CancellationError()));
            });

            cliProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                createProcessGroup: true,
                noExtensionVariables: true,
                env: options.env,
                stdoutCallback: (data) => { stdout.append(data); },
                stderrCallback: (data) => { stderr.append(data); },
                exitCallback: (code) => {
                    if (code !== 0) {
                        const retryArgs = this.tryGetNoLogoRetryArgs(args, stdout.value, stderr.value, command);
                        if (retryArgs) {
                            settle(() => {
                                this.runCliCommand(command, retryArgs, options).then(resolve, reject);
                            });
                            return;
                        }

                        settle(() => reject(new AspireCliFailedError(command, code, stdout.value, stderr.value)));
                        return;
                    }

                    settle(() => resolve({ stdout: stdout.value, stderr: stderr.value }));
                },
                errorCallback: (error) => {
                    settle(() => reject(new AspireCliNotInstalledError(error.message)));
                },
            });
            if (settledBeforeTracking) {
                // Already settled from inside spawnCliProcess, so settle() could not see this handle:
                // never track it, and terminate it if it is somehow still alive (a cancellation or
                // timeout that raced the spawn would otherwise orphan a live process).
                if (cliProcess.exitCode === null && !cliProcess.killed) {
                    terminateCliProcess(cliProcess, command);
                }

                return;
            }

            this._oneShotProcesses.add(cliProcess);
        });
    }

    stopOneShotProcesses(): void {
        for (const process of this._oneShotProcesses) {
            terminateCliProcess(process, 'one-shot aspire command');
        }
        this._oneShotProcesses.clear();
    }

    dispose(): void {
        this.stopOneShotProcesses();
    }
}

export class LimitedOutputBuffer {
    private readonly _marker: string;
    private readonly _headLimit: number;
    private readonly _tailLimit: number;
    private _head = '';
    private _tail = '';
    private _truncated = false;

    constructor(private readonly _limit: number | null) {
        if (_limit === null) {
            this._marker = '';
            this._headLimit = 0;
            this._tailLimit = 0;
            return;
        }

        this._marker = getOutputTruncationMarker(_limit);
        const available = Math.max(_limit - this._marker.length, 0);
        this._headLimit = Math.ceil(available / 2);
        this._tailLimit = available - this._headLimit;
    }

    append(data: string): void {
        if (this._limit === null) {
            this._head += data;
            return;
        }

        if (!this._truncated) {
            const combined = this._head + data;
            if (combined.length <= this._limit) {
                this._head = combined;
                return;
            }

            this._head = combined.slice(0, this._headLimit);
            this._tail = takeLast(combined, this._tailLimit);
            this._truncated = true;
            return;
        }

        this._tail = takeLast(this._tail + data, this._tailLimit);
    }

    get value(): string {
        if (!this._truncated) {
            return this._head;
        }

        return `${this._head}${this._marker}${this._tail}`;
    }
}

function getOutputTruncationMarker(limit: number): string {
    const marker = `\n${aspireCommandOutputTruncated(limit)}\n`;

    return marker.length <= limit ? marker : marker.slice(0, limit);
}

function takeLast(value: string, count: number): string {
    return count === 0 ? '' : value.slice(-count);
}

export function parseCliJsonOutput<T>(stdout: string): T {
    try {
        return JSON.parse(stdout);
    } catch (error) {
        // Some CLI invocations can emit startup diagnostics before the final JSON payload:
        //   Starting AppHost...
        //   {"resources":[{"name":"api", ...}]}
        // Parse the whole output first for the normal deterministic path, then fall back to
        // the last JSON-looking line so older or chatty CLIs do not poison the snapshot.
        for (const line of stdout.split(/\r?\n/).reverse()) {
            const trimmed = line.trim();
            if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
                try {
                    return JSON.parse(trimmed);
                } catch {
                    // Keep scanning in case the CLI wrote a JSON-looking diagnostic after the payload.
                }
            }
        }

        throw error;
    }
}

export function isDescribeUnsupportedOutput(nonJsonLines: readonly string[], stderr: string): boolean {
    const lines = [...nonJsonLines, ...stderr.split(/\r?\n/)];
    const output = lines.join('\n');
    if (!output) {
        return false;
    }

    // The surrounding help/error text and placeholder names are localized by System.CommandLine,
    // but the command name and bracket/angle syntax are stable. Older CLIs that do not support
    // `describe` either print top-level help such as:
    //   Uso:
    //   aspire <comando> [opciones]
    // or reject a stable token from the attempted invocation, such as `describe` or `--follow`.
    const normalizedOutput = output.toLowerCase();
    return lines.some(isAspireCommandHelpSyntaxLine)
        || (normalizedOutput.includes('usage:') && normalizedOutput.includes('commands:'))
        || lines.some(isRejectedDescribeInvocationLine);
}

// The tokens the extension itself sends when it starts a describe stream (see
// AppHostDataRepository._startDescribe). Compatibility detection is scoped to these because only a
// CLI that rejects one of them is too old to describe.
const describeInvocationTokens: readonly string[] = ['describe', '--follow', '--format', '--apphost', '--include-disabled-commands', noLogoOption];

// Recognizes a CLI line that rejects part of the describe invocation the extension sent, e.g.:
//   English:  Unrecognized command or argument 'describe'.
//   Spanish:  No se encuentra el recurso '--follow'.
//   Unquoted: Unrecognized command or argument --follow
// Matching happens per line and only for the extension's own tokens, because a *current* CLI can
// fail for reasons that quote a user-supplied option, e.g. an AppHost that reports
// `Unrecognized command or argument '--publisher'.`. Treating those as an unsupported `describe`
// would replace the real error with an "update the Aspire CLI" banner and mark it a CLI-wide
// compatibility failure, hiding the actual problem.
function isRejectedDescribeInvocationLine(line: string): boolean {
    // A line that quotes an option the extension never passed is a report about that option, so it
    // says nothing about whether the CLI understands `describe`.
    if (getQuotedOptionTokens(line).some(token => !describeInvocationTokens.includes(token))) {
        return false;
    }

    // The rejection wording is localized but the quoted token is not, so a quoted token of ours is
    // enough on its own and keeps this detection locale-independent.
    if (describeInvocationTokens.some(token => containsQuotedCliToken(line, token))) {
        return true;
    }

    // Some CLIs echo the rejected token unquoted. Those only count together with the (English)
    // rejection wording, because a bare token can otherwise appear in ordinary diagnostic prose.
    const normalizedLine = line.toLowerCase();
    const reportsUnrecognizedToken = normalizedLine.includes('unknown command')
        || normalizedLine.includes('unrecognized command')
        || normalizedLine.includes('unrecognized option')
        || normalizedLine.includes('is not a recognized command');

    return reportsUnrecognizedToken && describeInvocationTokens.some(token => containsBareCliToken(line, token));
}

function isAspireCommandHelpSyntaxLine(line: string): boolean {
    return /^aspire(?:\.exe)?\s+(?:<[^>]+>|\[[^\]]+\])(?:\s|$)/i.test(normalizeResourceCommandStatusLine(line));
}

const cliTokenQuotePattern = `[\\'"\`\\u2018\\u2019\\u201C\\u201D]`;

function getQuotedOptionTokens(line: string): string[] {
    const quotedTokenPattern = new RegExp(`${cliTokenQuotePattern}([^\\'"\`\\u2018\\u2019\\u201C\\u201D]+)${cliTokenQuotePattern}`, 'g');

    return [...line.matchAll(quotedTokenPattern)]
        .map(match => match[1].toLowerCase())
        .filter(token => token.startsWith('-'));
}

function containsQuotedCliToken(output: string, token: string): boolean {
    return new RegExp(`${cliTokenQuotePattern}${escapeRegExp(token)}${cliTokenQuotePattern}`, 'i').test(output);
}

function containsBareCliToken(output: string, token: string): boolean {
    return new RegExp(`(^|\\s)${escapeRegExp(token)}($|[\\s,.;:)\\]])`, 'i').test(output);
}

function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export function isIncludeDisabledCommandsUnsupportedOutput(nonJsonLines: readonly string[], stderr: string): boolean {
    // This is only consulted after a describe attempt produced no resource data, so any
    // non-JSON/stderr output here is diagnostic text rather than successful output. When the
    // CLI accepts `--include-disabled-commands` it streams JSON resources and never echoes the
    // flag name back, so the literal flag token only appears when the CLI is reporting that it
    // does not recognize the option, e.g.:
    //   English:  Unrecognized command or argument '--include-disabled-commands'.
    //   Spanish:  No se encuentra el recurso '--include-disabled-commands'.
    // The flag token itself is never localized, so detecting on its presence keeps this fallback
    // locale-independent — matching on translated phrases like "unrecognized option" would miss
    // non-English CLI output (e.g. via ASPIRE_LOCALE_OVERRIDE or the system locale).
    const output = [...nonJsonLines, stderr].join('\n');
    return output.includes('--include-disabled-commands');
}
