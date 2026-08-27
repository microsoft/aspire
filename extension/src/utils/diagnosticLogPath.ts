import * as path from 'path';

export function getAbsolutePathSuffix(text: string): string | undefined {
    for (let index = 0; index < text.length; index++) {
        if (index > 0 && !/\s/u.test(text[index - 1])) {
            continue;
        }

        const candidate = text.slice(index).trim();
        if (path.isAbsolute(candidate)) {
            return candidate;
        }
    }

    return undefined;
}

export function getDiagnosticLogPath(line: string, icon: string, englishPrefix: string): string | undefined {
    if (line.startsWith(icon)) {
        return getAbsolutePathSuffix(line.slice(icon.length).trim());
    }

    if (line.startsWith(englishPrefix)) {
        return getAbsolutePathSuffix(line.slice(englishPrefix.length).trim());
    }

    return undefined;
}
