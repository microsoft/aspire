import {
    AppHostDataRepository,
    AppHostDisplayInfo,
    ResourceJson,
    isMatchingAppHostPath,
} from './AppHostDataRepository';

export interface ResourceElementRef {
    resource: ResourceJson;
    appHostPid: number | null;
    appHostPath?: string;
}

export function findLatestResourceForElement(repository: AppHostDataRepository, element: ResourceElementRef): ResourceJson | undefined {
    const resources = findLatestResourcesForElement(repository, element);
    return resources?.find(resource => resource.name === element.resource.name);
}

export function findLatestResourcesForElement(repository: AppHostDataRepository, element: ResourceElementRef): readonly ResourceJson[] | undefined {
    const workspaceResources = [...repository.workspaceResources];
    const selectedAppHostPath = repository.workspaceAppHost?.appHostPath ?? repository.workspaceAppHostPath;

    if (element.appHostPath) {
        const matchingAppHosts = repository.appHosts.filter(appHost => isMatchingAppHostPath(appHost.appHostPath, element.appHostPath!));
        const appHostByPid = element.appHostPid !== null
            ? matchingAppHosts.find(appHost => appHost.appHostPid === element.appHostPid)
            : undefined;
        const appHost = appHostByPid ?? (matchingAppHosts.length === 1 ? matchingAppHosts[0] : undefined);
        if (appHost) {
            if (workspaceResources.length > 0 && selectedAppHostPath && isMatchingAppHostPath(appHost.appHostPath, selectedAppHostPath) && hasNoResources(appHost.resources)) {
                return workspaceResources;
            }

            return appHost.resources ?? [];
        }

        if (matchingAppHosts.length > 1) {
            return undefined;
        }

        if (!selectedAppHostPath || !isMatchingAppHostPath(element.appHostPath, selectedAppHostPath)) {
            return undefined;
        }

        return workspaceResources.length > 0
            ? workspaceResources
            : repository.workspaceAppHost?.resources ?? [];
    }

    const appHost = findAppHostForResource(repository, element);

    if (appHost && workspaceResources.length > 0 && selectedAppHostPath && isMatchingAppHostPath(appHost.appHostPath, selectedAppHostPath) && hasNoResources(appHost.resources)) {
        return workspaceResources;
    }

    if (appHost) {
        return appHost.resources ?? [];
    }

    return element.appHostPid === null ? workspaceResources : undefined;
}

export function findAppHostForResource(repository: AppHostDataRepository, element: ResourceElementRef): AppHostDisplayInfo | undefined {
    return element.appHostPid !== null
        ? repository.appHosts.find(appHost => appHost.appHostPid === element.appHostPid)
        : undefined;
}

export function getAppHostPathForResource(repository: AppHostDataRepository, element: ResourceElementRef): string | undefined {
    return element.appHostPath ?? findAppHostForResource(repository, element)?.appHostPath ?? repository.workspaceAppHostPath;
}

function hasNoResources(resources: readonly ResourceJson[] | null | undefined): boolean {
    return resources === undefined || resources === null || resources.length === 0;
}
