import { terminalCommandArgumentControlCharacters } from '../loc/strings';
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
