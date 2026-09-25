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

export function getCmdShimCommandInterpreter(): string {
    return process.env.ComSpec ?? 'cmd.exe';
}

// Validation remains in cmdShim.ts so user-facing failures use the extension's localized
// message. Keeping the construction itself dependency-free lets E2E inspect the exact
// process argv using the same implementation that production launches.
export function getCmdShimSpawnCommand(command: string, args: readonly string[]): CmdShimSpawnCommand {
    const commandArgs = [...args];
    return {
        command: getCmdShimCommandInterpreter(),
        args: ['/d', '/v:off', '/s', '/c', buildCmdWrapperCommand(command, commandArgs)],
        diagnosticArgs: [command, ...commandArgs],
        windowsVerbatimArguments: true,
    };
}

function buildCmdWrapperCommand(command: string, args: string[]): string {
    // The outer quote pair is consumed by `/s`, leaving the inner per-argument quoting
    // intact for cmd.exe. See `cmd /?` for the `/s` first/last quote stripping rule.
    return `"${[quoteCmdArgument(command), ...args.map(quoteCmdArgument)].join(' ')}"`;
}

export function quoteCmdArgument(value: string): string {
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
