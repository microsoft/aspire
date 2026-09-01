import * as path from 'path';

const appHostSourceFileNames = ['apphost.cs', 'program.cs'];

// Only Windows guarantees case-insensitive paths. macOS volumes can be formatted
// case-sensitive, so folding case there would collapse genuinely distinct paths.
export function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}

export function isSamePath(left: string, right: string): boolean {
    const comparison = process.platform === 'win32'
        ? 'case-insensitive'
        : 'case-sensitive';
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    return comparison === 'case-insensitive'
        ? resolvedLeft.toLowerCase() === resolvedRight.toLowerCase()
        : resolvedLeft === resolvedRight;
}

export function isProjectFile(value: string): boolean {
    return path.extname(value).toLowerCase() === '.csproj';
}

export function isAppHostSourceFile(value: string): boolean {
    return appHostSourceFileNames.includes(path.basename(value).toLowerCase());
}

export function isProjectFileToSourceFileMatch(left: string, right: string): boolean {
    return (isProjectFile(left) && isAppHostSourceFile(right)) || (isAppHostSourceFile(left) && isProjectFile(right));
}

export function isAppHostPathUnderFolder(appHostPath: string | undefined, folderPath: string | undefined): boolean {
    if (!appHostPath || !folderPath) {
        return false;
    }

    const normalizedAppHostPath = getComparisonKey(path.normalize(appHostPath));
    const normalizedFolderPath = getComparisonKey(path.normalize(folderPath));
    if (normalizedAppHostPath === normalizedFolderPath) {
        return false;
    }

    const folderPrefix = normalizedFolderPath.endsWith(path.sep) ? normalizedFolderPath : `${normalizedFolderPath}${path.sep}`;
    return normalizedAppHostPath.startsWith(folderPrefix);
}

export function isSameAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    return getComparisonKey(path.normalize(left)) === getComparisonKey(path.normalize(right));
}
