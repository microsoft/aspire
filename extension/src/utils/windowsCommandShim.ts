export interface WindowsCommandShimSpawnCommand {
    command: string;
    args: string[];
    windowsVerbatimArguments?: boolean;
}

export function shouldUseWindowsCommandShim(command: string): boolean {
    return process.platform === 'win32' && /\.(?:cmd|bat)$/i.test(command);
}

export function getWindowsCommandShimSpawnCommand(command: string, args: readonly string[] = []): WindowsCommandShimSpawnCommand {
    return {
        command: process.env.ComSpec ?? 'cmd.exe',
        args: ['/d', '/s', '/c', createWindowsCommandShimCommandLine(command, args)],
        windowsVerbatimArguments: true,
    };
}

function createWindowsCommandShimCommandLine(command: string, args: readonly string[]): string {
    // cmd.exe parses everything after /c as one command line, not as argv. Build a quoted
    // command string and pass it verbatim so paths like C:\R&D\aspire.cmd are not split at '&'.
    return ['call', quoteWindowsCommandArgument(command), ...args.map(quoteWindowsCommandArgument)].join(' ');
}

function quoteWindowsCommandArgument(value: string): string {
    return `"${value.replace(/"/g, '""').replace(/%/g, '%%')}"`;
}
