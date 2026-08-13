import { realpathSync } from 'node:fs';
import * as path from 'path';
import { ResourceJson, AppHostDisplayInfo } from '../views/AppHostDataRepository';

export interface ResourceMatch {
    resource: ResourceJson;
    appHost: AppHostDisplayInfo;
}

export function findResourceState(
    appHosts: readonly AppHostDisplayInfo[],
    resourceName: string,
): ResourceMatch | undefined {
    for (const appHost of appHosts) {
        if (!appHost.resources) {
            continue;
        }
        // Prefer displayName because the runtime `name` field includes a random suffix
        // (e.g., "postgres-fbnfwdfv"), whereas displayName matches the source code name.
        const resource =
            appHost.resources.find((r: ResourceJson) => r.displayName === resourceName) ??
            appHost.resources.find((r: ResourceJson) => r.name === resourceName);
        if (resource) {
            return { resource, appHost };
        }
    }
    return undefined;
}

export function findWorkspaceResourceState(
    workspaceResources: readonly ResourceJson[],
    workspaceAppHostPath: string,
): (resourceName: string) => ResourceMatch | undefined {
    return (resourceName: string) => {
        const resource =
            workspaceResources.find((r: ResourceJson) => r.displayName === resourceName) ??
            workspaceResources.find((r: ResourceJson) => r.name === resourceName);
        if (resource) {
            return {
                resource,
                appHost: {
                    appHostPath: workspaceAppHostPath,
                    appHostPid: 0,
                    cliPid: null,
                    dashboardUrl: null,
                    resources: [...workspaceResources],
                },
            };
        }
        return undefined;
    };
}

export function matchesAppHostPathOrDirectory(documentPath: string, appHostPath: string | undefined): boolean {
    if (!appHostPath) {
        return false;
    }

    if (pathsMatch(documentPath, appHostPath)) {
        return true;
    }

    // VS Code preserves the symlink path used to open a workspace, while the CLI
    // reports the canonical AppHost path. Avoid filesystem I/O for the normal case,
    // then retry with real paths only when the normalized strings do not match.
    const canonicalDocumentPath = tryGetCanonicalPath(documentPath);
    const canonicalAppHostPath = tryGetCanonicalPath(appHostPath);
    return canonicalDocumentPath !== undefined
        && canonicalAppHostPath !== undefined
        && pathsMatch(canonicalDocumentPath, canonicalAppHostPath);
}

function pathsMatch(documentPath: string, appHostPath: string): boolean {
    const normalizedDocumentPath = getComparisonKey(path.normalize(documentPath));
    const normalizedAppHostPath = getComparisonKey(path.normalize(appHostPath));
    return normalizedAppHostPath === normalizedDocumentPath
        || getComparisonKey(path.dirname(normalizedAppHostPath)) === getComparisonKey(path.dirname(normalizedDocumentPath));
}

function tryGetCanonicalPath(value: string): string | undefined {
    try {
        return realpathSync.native(value);
    } catch {
        // Canonicalization is a best-effort fallback. Broken links, permissions,
        // and other filesystem failures must not break CodeLens or gutter updates.
        return undefined;
    }
}

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}
