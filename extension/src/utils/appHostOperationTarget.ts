import * as vscode from 'vscode';

import { getCliPathTargetForUri, type CliPathResolutionTarget } from './cliPathVariables';

/**
 * One AppHost operation, split into the physical AppHost it acts on and the path whose owning
 * workspace folder decides which CLI and configuration apply to it.
 *
 * The two are not the same question and must not be answered from the same string. *What* to act
 * on has to be the captured physical path, because the name the caller used can be repointed
 * while the operation is in flight. *Which CLI, and whose settings* has to stay the path the
 * caller named, because that is what decides the owning workspace folder: a workspace root can be
 * a symlink or a linked worktree, in which case the physical path can be outside every open
 * folder, and resolving scope from it would silently fall back to window-level settings and run a
 * different `aspire.cliPath` than the folder configured. In a multi-root workspace it would pick
 * the wrong folder's configuration instead.
 */
export interface AppHostOperationTarget {
    /** Physical AppHost path the operation is performed against; what reaches CLI argv. */
    readonly operationPath: string;
    /** Path whose owning workspace folder decides CLI resolution and configuration scope. */
    readonly scopePath: string;
}

/**
 * Pairs the AppHost an operation acts on with the path its scope is resolved from.
 *
 * Both are required. An operation that scopes itself from its own operation path is exactly the
 * defect this type exists to make impossible to write by accident.
 */
export function createAppHostOperationTarget(operationPath: string, scopePath: string): AppHostOperationTarget {
    return { operationPath, scopePath };
}

/** Resolves the CLI/configuration scope an AppHost operation runs under. */
export function getCliPathTargetForAppHostOperation(target: AppHostOperationTarget): CliPathResolutionTarget {
    return getCliPathTargetForUri(vscode.Uri.file(target.scopePath));
}
