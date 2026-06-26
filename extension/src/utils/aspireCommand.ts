export function isBareAspireCommand(value: string): boolean {
    if (value.includes('/') || value.includes('\\')) {
        return false;
    }

    return /^(?:aspire|aspire\.exe|aspire\.cmd|aspire\.bat)$/i.test(value);
}
