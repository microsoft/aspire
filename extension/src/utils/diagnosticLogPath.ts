import * as path from 'path';

const logFileExtension = '.log';

export function getAbsoluteLogFilePath(text: string): string | undefined {
    const lowerText = text.toLowerCase();

    for (let index = 0; index < text.length; index++) {
        if (index > 0 && !/\s/u.test(text[index - 1])) {
            continue;
        }

        // FileLoggerProvider.GenerateLogFilePath and AppHostLauncher.GenerateChildLogFilePath
        // generate diagnostic filenames ending in ".log". Translations can place text after
        // the {0} placeholder, and Korean appends that text without whitespace:
        //   Protokolle unter C:\...\cli.log anzeigen.
        //   C:\...\cli.log에서 로그 보기
        // Use the extension as the end boundary instead of treating the localized suffix as path text.
        let extensionIndex = lowerText.lastIndexOf(logFileExtension);
        while (extensionIndex >= index) {
            const candidate = text.slice(index, extensionIndex + logFileExtension.length).trim();
            if (path.isAbsolute(candidate)) {
                return candidate;
            }

            extensionIndex = lowerText.lastIndexOf(logFileExtension, extensionIndex - 1);
        }
    }

    return undefined;
}

export function getDiagnosticLogPath(line: string, icon: string, englishPrefix: string): string | undefined {
    if (line.startsWith(icon)) {
        return getAbsoluteLogFilePath(line.slice(icon.length).trim());
    }

    if (line.startsWith(englishPrefix)) {
        return getAbsoluteLogFilePath(line.slice(englishPrefix.length).trim());
    }

    return undefined;
}
