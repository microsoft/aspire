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
    if (!hasRootNoLogoOption(args)) {
        return false;
    }

    // This helper is only used after a hidden CLI probe failed. Older CLIs localize the
    // unsupported-option message, but the rejected flag token itself is stable, e.g.:
    //   English:  Unrecognized command or argument '--nologo'.
    //   Spanish:  No se encuentra el recurso '--nologo'.
    // Matching the token keeps the retry locale-independent while the args guard scopes it to
    // commands where the extension actually passed `--nologo` as a root option.
    const combined = `${stdout}\n${stderr}`;
    return combined.includes(noLogoOption);
}

export function containsQuotedCliToken(output: string, token: string): boolean {
    const escapedToken = token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`[\\'"\`\\u2018\\u2019\\u201C\\u201D]${escapedToken}[\\'"\`\\u2018\\u2019\\u201C\\u201D]`).test(output);
}
