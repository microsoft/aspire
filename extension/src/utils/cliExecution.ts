import { assertNoTerminalControlCharacters } from './AspireTerminalProvider';

export interface CliExecutionCommand {
    /** Executable to spawn. On Windows for .cmd/.bat shims this is cmd.exe; otherwise the CLI path itself. */
    file: string;
    /**
     * argv to pass to spawn/execFile. On Windows for .cmd/.bat shims this is a fixed cmd.exe
     * prologue followed by a single pre-built command-line string that cmd.exe parses with /c.
     * Must be spawned with windowsVerbatimArguments: true so Node does not re-quote it.
     */
    args: string[];
    /**
     * Args suitable for human-readable diagnostics. When the cmd.exe wrapper is used these
     * reflect the original CLI invocation (e.g. `call <cliPath> <arg1> <arg2>`) rather than
     * the wrapped /c command line, which is easier to log and redact.
     */
    diagnosticArgs?: string[];
    windowsVerbatimArguments: boolean;
}

/**
 * Builds the spawn arguments for executing the Aspire CLI.
 *
 * On Windows, .NET global tools and bundle installs sometimes expose the CLI as a `.cmd`/`.bat`
 * shim instead of a native executable. Node's `child_process.spawn`/`execFile` with `shell: false`
 * cannot launch a .cmd/.bat directly, so those paths are routed through `cmd.exe /d /v:off /s /c`
 * with a single pre-quoted command line and `windowsVerbatimArguments: true`. Native `.exe` paths
 * and non-Windows platforms execute directly.
 */
export function getCliExecutionCommand(cliPath: string, args: string[]): CliExecutionCommand {
    if (process.platform === 'win32' && /\.(?:cmd|bat)$/i.test(cliPath)) {
        // cmd.exe receives this path as one `/c` command string, not an argv array.
        // Reject terminal controls before quoting so CR/LF and ETX cannot split the wrapper
        // invocation or cancel the command before cmd parsing reaches the quotes.
        assertNoCmdWrapperControlCharacters([cliPath, ...args]);

        return {
            file: process.env.ComSpec ?? 'cmd.exe',
            args: ['/d', '/v:off', '/s', '/c', buildCmdWrapperCommand(cliPath, args)],
            diagnosticArgs: ['call', cliPath, ...args],
            windowsVerbatimArguments: true,
        };
    }

    return { file: cliPath, args, windowsVerbatimArguments: false };
}

function assertNoCmdWrapperControlCharacters(values: readonly string[]): void {
    for (const value of values) {
        assertNoTerminalControlCharacters(value);
    }
}

function buildCmdWrapperCommand(command: string, args: string[]): string {
    return ['call', quoteCmdArgument(command), ...args.map(quoteCmdArgument)].join(' ');
}

function quoteCmdArgument(value: string): string {
    // The wrapper command is executed as:
    //   cmd.exe /d /v:off /s /c call "aspire.cmd" "<arg>" ...
    // Many .cmd shims then forward arguments to a native executable with `%*`, for example:
    //   "node.exe" "aspire.js" %*
    // Because `call` reparses the command before the target .cmd sees `%*`, percent signs need
    // two rounds of batch escaping. `%%%%PRIVATE_FEED%%%%` reaches the shim as
    // `%%PRIVATE_FEED%%`, then the shim's `%*` forwarding preserves `%PRIVATE_FEED%` literally
    // instead of expanding the caller's PRIVATE_FEED environment variable.
    //
    // `%*` is parsed later by normal Windows argv rules, so trailing backslashes must also be
    // doubled before our closing quote (`"--path=C:\temp\\" "next"`), and backslashes before
    // embedded quotes must be doubled before cmd's doubled-quote escape.
    const valueWithEscapedPercents = value.replace(/%/g, '%%%%');
    let quotedValue = '';
    let backslashCount = 0;

    for (const character of valueWithEscapedPercents) {
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
