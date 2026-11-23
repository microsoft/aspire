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

/**
 * Keys an AppHost path *lexically*, applying the same normalization and platform case rules
 * {@link isSameAppHostPath} compares with. Use this when paths must key a map rather than be
 * compared pairwise.
 *
 * Deliberately named for what it does rather than for AppHost identity: it never touches the
 * filesystem, so two spellings of one AppHost - a symlink and its target, or a path and its
 * `..`-free equivalent under a symlinked root - key differently here. Keying AppHost *identity*
 * is `getAppHostPathComparisonKey` in `utils/appHostIdentity`, which canonicalizes first; the two
 * are not interchangeable and confusing them silently weakens an identity check into a string
 * comparison an alias can influence.
 */
export function getLexicalAppHostPathKey(value: string): string {
    return getComparisonKey(path.normalize(value));
}

export function isSameAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    return getLexicalAppHostPathKey(left) === getLexicalAppHostPathKey(right);
}
