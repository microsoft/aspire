import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * Reads the git commit SHA from the .version file bundled with the extension.
 * Returns 'unknown' if the file is missing or unreadable.
 */
export function readGitCommitSha(context: vscode.ExtensionContext): string {
    return readVersionFile(context.extensionPath);
}

/**
 * Reads the .version file from the given extension root directory.
 * Exported for testing.
 */
export function readVersionFile(extensionRoot: string): string {
    try {
        const versionFilePath = path.join(extensionRoot, '.version');
        return fs.readFileSync(versionFilePath, 'utf8').trim();
    } catch {
        return 'unknown';
    }
}
