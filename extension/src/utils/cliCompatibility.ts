export const noLogoOption = '--nologo';

export function hasRootNoLogoOption(args: readonly string[]): boolean {
    const delimiterIndex = args.indexOf('--');
    const end = delimiterIndex === -1 ? args.length : delimiterIndex;

    return args.slice(0, end).includes(noLogoOption);
}

export function removeRootNoLogoOption(args: readonly string[]): string[] {
    const delimiterIndex = args.indexOf('--');
    const end = delimiterIndex === -1 ? args.length : delimiterIndex;
    const noLogoIndex = args.findIndex((arg, index) => index < end && arg === noLogoOption);

    if (noLogoIndex === -1) {
        return [...args];
    }

    return [...args.slice(0, noLogoIndex), ...args.slice(noLogoIndex + 1)];
}

// Match System.CommandLine's unrecognized-option error so plain mentions of `--nologo` in
// help text, diagnostic logs, or unrelated error messages do not trigger a spurious retry
// that hides the real failure. The canonical text is e.g.:
//   Unrecognized command or argument '--nologo'.
// See: https://learn.microsoft.com/dotnet/standard/commandline/ (System.CommandLine).
export function isNoLogoUnsupportedOutput(args: readonly string[], stdout: string, stderr: string): boolean {
    if (!hasRootNoLogoOption(args)) {
        return false;
    }

    const combined = `${stdout}\n${stderr}`;
    return combined.includes(noLogoOption) && /Unrecognized\s+(command\s+or\s+argument|option)/i.test(combined);
}
