export interface CliExecutionCommand {
    file: string;
    args: string[];
    windowsVerbatimArguments: boolean;
}

export function getCliExecutionCommand(cliPath: string, args: string[]): CliExecutionCommand {
    if (shouldUseCmdForCliPath(cliPath)) {
        // .cmd/.bat files are interpreted by cmd.exe. Bare "aspire" on PATH may
        // resolve to a .cmd shim through PATHEXT, so route PATH lookup through
        // cmd.exe on Windows too. Explicit paths and arguments must be quoted for
        // cmd.exe because metacharacters like & are still parsed when shelling out.
        const command = cliPath === 'aspire' ? cliPath : escapeCmdArg(cliPath);

        return {
            file: process.env.ComSpec ?? 'cmd.exe',
            args: ['/d', '/c', 'call', command, ...args.map(escapeCmdArg)],
            windowsVerbatimArguments: true,
        };
    }

    return { file: cliPath, args, windowsVerbatimArguments: false };
}

function shouldUseCmdForCliPath(cliPath: string): boolean {
    return process.platform === 'win32' && (cliPath === 'aspire' || /\.(?:cmd|bat)$/i.test(cliPath));
}

function escapeCmdArg(value: string): string {
    return `"${value.replace(/"/g, '""')}"`;
}
