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

export function isNoLogoUnsupportedOutput(args: readonly string[], stdout: string, stderr: string): boolean {
    return hasRootNoLogoOption(args) && `${stdout}\n${stderr}`.includes(noLogoOption);
}
