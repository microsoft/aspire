import type * as vscode from 'vscode';

import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';

/**
 * The discovered AppHost identity that an editor-owned operation may use. The absolute
 * path remains internal; callers render only `displayPath` and return only `relativePath`
 * or `displayPath` in tool results.
 */
export interface AppHostTarget {
    readonly absolutePath: string;
    readonly relativePath: string;
    readonly displayPath: string;
}

export type AppHostTargetResolutionOutcome =
    | 'invalidInput'
    | 'unknownAppHost'
    | 'ambiguousAppHost'
    | 'discoveryFailed'
    | 'cancelled';

export type AppHostTargetResolution =
    | { readonly resolved: true; readonly target: AppHostTarget }
    | {
        readonly resolved: false;
        readonly outcome: AppHostTargetResolutionOutcome;
        readonly knownAppHosts?: readonly string[];
    };

/**
 * Narrow view of the registry the editor uses to discover AppHosts. Resolution never
 * turns a model selector into a path; it only compares it with entries from this registry.
 */
export interface AppHostTargetDiscoveryService {
    discover(
        workspaceFolder: vscode.WorkspaceFolder,
        forceRefresh?: boolean,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<readonly CandidateAppHostDisplayInfo[]>;
}

export interface AppHostTargetResolver {
    resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution>;
}
