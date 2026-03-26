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
