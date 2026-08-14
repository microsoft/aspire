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
