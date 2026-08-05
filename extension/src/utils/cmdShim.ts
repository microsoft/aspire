import { terminalCommandArgumentControlCharacters } from '../loc/strings';

/**
 * Shape describing how to launch a command, mirroring the subset of Node's
 * `child_process` options the extension needs to run Windows command shims.
 */
export interface CmdShimSpawnCommand {
    command: string;
    args: string[];
    /** Diagnostic-friendly argument list; the wrapped form is hard to read in logs. */
    diagnosticArgs?: string[];
    windowsVerbatimArguments?: boolean;
}

export function assertNoTerminalControlCharacters(value: string): void {
    // Shell quoting protects shell metacharacters after the command reaches the
    // shell. C0 controls are terminal input first: in sendText fallback, ETX can
    // abort the current line and CR/LF can submit following text as another
    // command before shell parsing can make those bytes inert. Tab is allowed
    // because shells treat it as ordinary whitespace inside quotes.
    if (/[\x00-\x08\x0A-\x1F\x7F]/.test(value)) {
        throw new Error(terminalCommandArgumentControlCharacters);
    }
}

/**
 * Windows `.cmd`/`.bat` shims are batch scripts, not executables. Node refuses to
 * spawn them without a shell since the CVE-2024-27980 fix
 * (https://github.com/nodejs/node/issues/52681), so they must go through cmd.exe.
 */
export function isCommandShimPath(command: string): boolean {
    return /\.(?:cmd|bat)$/i.test(command);
}

export function shouldWrapWithCmd(command: string): boolean {
    return process.platform === 'win32' && isCommandShimPath(command);
}

function getComSpec(): string {
    return process.env.ComSpec ?? 'cmd.exe';
}

function assertNoCmdWrapperControlCharacters(values: readonly string[]): void {
    for (const value of values) {
        assertNoTerminalControlCharacters(value);
    }
}

/**
 * Builds the cmd.exe invocation for a command shim when the caller can set
 * `windowsVerbatimArguments`. The whole command is passed as one `/c` string that this
 * module quotes itself, wrapped in an extra quote pair that `/s` strips, which is the
 * same shape Node uses for `shell: true`.
 *
 * `call` is deliberately not used. It re-parses its command line, which consumes a `^`
 * in the shim path even when the path is quoted, so `call` cannot launch a shim under a
 * directory such as `C:\tools\a^b`. Verified on Windows CI across `&`, `^`, `()` and
 * space directories.
 *
 * Quoting makes `&`, `^`, `|`, `<`, `>` and parentheses literal. Percent expansion is
 * an unavoidable limitation of routing a batch shim through a `cmd /c` command string.
 */
export function getCmdShimSpawnCommand(command: string, args: readonly string[]): CmdShimSpawnCommand {
    const commandArgs = [...args];
    // cmd.exe receives this path as one `/c` command string, not an argv array.
    // Reject terminal controls before quoting so CR/LF and ETX cannot split the wrapper
    // invocation or cancel the command before cmd parsing reaches the quotes.
    assertNoCmdWrapperControlCharacters([command, ...commandArgs]);

    return {
        command: getComSpec(),
        args: ['/d', '/v:off', '/s', '/c', buildCmdWrapperCommand(command, commandArgs)],
        diagnosticArgs: [command, ...commandArgs],
        windowsVerbatimArguments: true,
    };
}

/**
 * Builds the cmd.exe invocation for a command shim when the caller cannot set
 * `windowsVerbatimArguments`. VS Code 1.102's MCP launcher quotes shell-script
 * tokens only when they contain whitespace, so a path such as `C:\Users\a&b\aspire.cmd`
 * would otherwise be split at the ampersand.
 *
 * The argv shape survives libuv's quoting pass by caret-escaping both whitespace
 * and metacharacters. The caret forces cmd.exe's quote-stripping branch, then makes
 * the resulting unquoted token parse as one literal value. Percent expansion remains
 * an unavoidable limitation of routing a batch shim through a cmd.exe command string.
 */
export function getCmdShimSpawnCommandWithoutVerbatimArguments(command: string, args: readonly string[]): CmdShimSpawnCommand {
    const commandArgs = [...args];
    assertNoCmdWrapperControlCharacters([command, ...commandArgs]);
    if (commandArgs.some(argument => argument.length === 0 || /[ \t"]/.test(argument))) {
        throw new Error('The non-verbatim cmd.exe wrapper cannot safely quote arguments containing whitespace or quotes.');
    }

    return {
        command: getComSpec(),
        // `/s` is omitted because there is no outer quote pair to strip here. `call`
        // is omitted because its second parse consumes carets and breaks parenthesized paths.
        args: ['/d', '/v:off', '/c', ...[command, ...commandArgs].map(escapeCmdArgumentForLibuvQuoting)],
    };
}

function escapeCmdArgumentForLibuvQuoting(value: string): string {
    // libuv wraps a token containing whitespace in quotes. cmd.exe preserves those
    // quotes only when the quoted text contains no special characters; the caret we
    // add makes it strip the quotes and then consume each caret as an escape. This
    // handles paths combining spaces and metacharacters without windowsVerbatimArguments.
    // Escape cmd.exe's documented special characters that are legal in Windows
    // paths. Percent is intentionally excluded because caret escaping does not
    // prevent `%NAME%` expansion in a cmd /c command string.
    return value.replace(/[ \t()[\]{}!^`<>&|;,+'=@~]/g, match => `^${match}`);
}

function buildCmdWrapperCommand(command: string, args: string[]): string {
    // The outer quote pair is consumed by `/s`, leaving the inner per-argument quoting
    // intact for cmd.exe. See `cmd /?` for the `/s` first/last quote stripping rule.
    return `"${[quoteCmdArgument(command), ...args.map(quoteCmdArgument)].join(' ')}"`;
}

function quoteCmdArgument(value: string): string {
    // The wrapper command is executed as:
    //   cmd.exe /d /v:off /s /c ""aspire.cmd" "<arg>" ..."
    // Many .cmd shims then forward arguments to a native executable with `%*`, for example:
    //   "node.exe" "aspire.js" %*
    // Percent signs cannot be escaped in a `cmd /c` command string. `%%` only collapses
    // inside a batch file; using it here corrupts the path or argument before the shim runs.
    // Trailing backslashes must be doubled before our closing quote
    // (`"--path=C:\temp\\" "next"`), and backslashes before embedded quotes must be doubled
    // before cmd's doubled-quote escape.
    let quotedValue = '';
    let backslashCount = 0;

    for (const character of value) {
        if (character === '\\') {
            backslashCount++;
            continue;
        }

        if (character === '"') {
            quotedValue += '\\'.repeat(backslashCount * 2);
            backslashCount = 0;
            quotedValue += '""';
            continue;
        }

        quotedValue += '\\'.repeat(backslashCount);
        backslashCount = 0;
        quotedValue += character;
    }

    quotedValue += '\\'.repeat(backslashCount * 2);
    return `"${quotedValue}"`;
}
