import * as vscode from 'vscode';
import * as path from 'path';
import { isProjectFileToSourceFileMatch, isSameAppHostPath } from '../utils/paths/comparison';
import { isSameFileSystemEntry } from '../utils/paths/fileSystemIdentity';
import { AppHostDisplayInfo } from './appHostCliContracts';

export function isPathInWorkspace(filePath: string): boolean {
    return vscode.workspace.workspaceFolders?.some(workspaceFolder => {
        const relativePath = path.relative(workspaceFolder.uri.fsPath, filePath);
        return relativePath !== ''
            && !relativePath.startsWith('..')
            && !path.isAbsolute(relativePath);
    }) ?? false;
}

export function isMatchingAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    if (isSameFileSystemEntry(left, right)) {
        return true;
    }

    const normalizedLeft = path.normalize(left);
    const normalizedRight = path.normalize(right);

    // `aspire extension get-apphosts` resolves a project file while `aspire ps`
    // can report the AppHost source file. Match by directory only for that
    // project/source-file shape so sibling AppHost projects don't collapse into
    // the same workspace AppHost.
    return isSameFileSystemEntry(path.dirname(normalizedLeft), path.dirname(normalizedRight))
        && isProjectFileToSourceFileMatch(normalizedLeft, normalizedRight);
}

export function isMatchingAppHostInstance(left: AppHostDisplayInfo, right: AppHostDisplayInfo): boolean {
    return left.appHostPid === right.appHostPid
        && isSameAppHostPath(left.appHostPath, right.appHostPath);
}
