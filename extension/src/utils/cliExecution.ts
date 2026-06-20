export interface CliExecutionCommand {
    file: string;
    args: string[];
    windowsVerbatimArguments: boolean;
}

export function getCliExecutionCommand(cliPath: string, args: string[]): CliExecutionCommand {
    if (shouldUseCmdForCliPath(cliPath)) {
        // .cmd/.bat files are interpreted by cmd.exe. cmd.exe receives a single
        // command string after /c, so quote for Windows argv parsing first and then
        // escape cmd metacharacters such as &, %, and trailing backslashes.
        const command = escapeCmdCommand(cliPath);
        const commandLine = [command, ...args.map(escapeCmdArgument)].join(' ');

        return {
            file: process.env.ComSpec ?? 'cmd.exe',
            args: ['/d', '/s', '/c', `"${commandLine}"`],
            windowsVerbatimArguments: true,
        };
    }

    return { file: cliPath, args, windowsVerbatimArguments: false };
}

function shouldUseCmdForCliPath(cliPath: string): boolean {
    return process.platform === 'win32' && /\.(?:cmd|bat)$/i.test(cliPath);
}

const cmdMetaCharsExpression = /([()%!^"`<>&|;, *?\[\]])/g;

function escapeCmdCommand(value: string): string {
    return value.replace(cmdMetaCharsExpression, '^$1');
}

function escapeCmdArgument(value: string): string {
    let arg = value.replace(/(\\*)"/g, '$1$1\\"');
    arg = arg.replace(/(\\+)$/g, '$1$1');
    arg = `"${arg}"`;

    return arg.replace(cmdMetaCharsExpression, '^$1');
}
