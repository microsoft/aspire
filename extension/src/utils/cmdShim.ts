import { cmdShimPercentArgument, terminalCommandArgumentControlCharacters } from '../loc/strings';
import { getCmdShimSpawnCommand as buildCmdShimSpawnCommand, getCmdShimCommandInterpreter, quoteCmdArgument } from './cmdShimCommand';
import type { CmdShimSpawnCommand } from './cmdShimCommand';
export { isCommandShimPath, quoteCmdArgument, shouldWrapWithCmd } from './cmdShimCommand';
export type { CmdShimSpawnCommand } from './cmdShimCommand';

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

function assertCmdWrapperValues(values: readonly string[]): void {
    for (const value of values) {
        assertNoTerminalControlCharacters(value);
    }

    // cmd.exe expands `%NAME%` before the batch shim receives `%*`. A single unmatched percent is
    // literal, but a pair can span quoted argv tokens because cmd.exe parses the full command.
    if (/%[^%]*%/.test(values.join(' '))) {
        throw new Error(cmdShimPercentArgument);
    }
}

/**
 * True when VS Code's MCP stdio launcher can spawn a Windows command shim directly, without the
 * cmd.exe wrapper below.
 *
 * VS Code resolves the executable, double-quotes any token that contains a space, and hands the
 * joined string to `cmd.exe /d /s /c` with `windowsVerbatimArguments`. See:
 * https://github.com/microsoft/vscode/blob/1.102.3/src/vs/workbench/api/node/extHostMcpNode.ts#L141-L167
 *
 * That makes a token safe when it is quoted - cmd.exe treats `&`, `^`, `|`, `<`, `>` and
 * parentheses inside quotes literally, and a batch shim's `%1` split keeps a quoted value whole -
 * or when it holds nothing cmd.exe treats specially in the first place. The wrapper is only
 * required for the remaining case: a metacharacter in a token with no whitespace, which VS Code
 * leaves unquoted.
 *
 * Preferring this path matters because the wrapper cannot carry whitespace in a forwarded
 * argument. cmd.exe strips carets before a batch file splits `%1`..`%9`, so a caret-escaped space
 * would silently become an argument separator, and AppHost project paths routinely contain spaces.
 *
 * Percent-delimited values are excluded because cmd.exe expands them before the shim runs, and
 * quoting does not suppress that expansion.
 */
export function canVsCodeQuoteCommandShimLaunch(command: string, args: readonly string[]): boolean {
    return [command, ...args].every(isVsCodeQuotableCommandShimToken);
}

function isVsCodeQuotableCommandShimToken(value: string): boolean {
    // An empty token disappears from the joined command string, and a double quote would reopen
    // the quoting VS Code applies. Neither is representable, so neither is safe.
    if (value.length === 0 || value.includes('"') || value.includes('%')) {
        return false;
    }

    // Terminal controls are terminal input before they are shell input, so they are rejected on
    // both launch paths rather than being made inert by quoting.
    if (/[\x00-\x08\x0A-\x1F\x7F]/.test(value)) {
        return false;
    }

    return value.includes(' ') || !/[&|<>^()!,;=@\t]/.test(value);
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
 * Quoting makes `&`, `^`, `|`, `<`, `>` and parentheses literal. Percent-delimited values are
 * rejected because cmd.exe expands them before the shim receives its arguments.
 */
export function getCmdShimSpawnCommand(command: string, args: readonly string[]): CmdShimSpawnCommand {
    const commandArgs = [...args];
    // cmd.exe receives this path as one `/c` command string, not an argv array.
    // Reject terminal controls before quoting so CR/LF and ETX cannot split the wrapper
    // invocation or cancel the command before cmd parsing reaches the quotes.
    // Percent expansion in the command path remains intentional so settings such as
    // `%ASPIRE_HOME%\aspire.cmd` keep working; only forwarded argv must remain byte-for-byte exact.
    assertNoTerminalControlCharacters(command);
    assertCmdWrapperValues(commandArgs);

    return buildCmdShimSpawnCommand(command, commandArgs);
}

/**
 * Builds the cmd.exe invocation for a command shim when the caller cannot set
 * `windowsVerbatimArguments`. VS Code 1.102's MCP launcher quotes shell-script
 * tokens only when they contain whitespace, so a path such as `C:\Users\a&b\aspire.cmd`
 * would otherwise be split at the ampersand.
 *
 * The argv shape survives libuv's quoting pass by caret-escaping both whitespace
 * and metacharacters. The caret forces cmd.exe's quote-stripping branch, then makes
 * the resulting unquoted token parse as one literal value. Percent-delimited values are rejected
 * because cmd.exe expands them before the shim receives its arguments.
 */
export function getCmdShimSpawnCommandWithoutVerbatimArguments(command: string, args: readonly string[]): CmdShimSpawnCommand {
    const commandArgs = [...args];
    assertNoTerminalControlCharacters(command);
    assertCmdWrapperValues(commandArgs);
    if (commandArgs.some(argument => argument.length === 0 || /[ \t"]/.test(argument))) {
        throw new Error('The non-verbatim cmd.exe wrapper cannot safely quote arguments containing whitespace or quotes.');
    }

    return {
        command: getCmdShimCommandInterpreter(),
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
