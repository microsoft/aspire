import * as path from 'path';

import { AspireExtensionContext } from './AspireExtensionContext';
import { AppHostLaunchService } from './services/AppHostLaunchService';
import type { AspireAppHostState, AspireExtensionStateSnapshot, AspireResourceCommandState, AspireResourceState, AspireResourceUrlState } from './types/extensionApi';
import { AspireAppHostTreeProvider } from './views/AspireAppHostTreeProvider';
import { AppHostDataRepository, AppHostDisplayInfo, ResourceJson, isMatchingAppHostPath } from './views/AppHostDataRepository';

export function createStateSnapshot(
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  extensionContext: AspireExtensionContext,
  includeSensitiveDashboardUrls = false,
): AspireExtensionStateSnapshot {
  return {
    viewMode: dataRepository.viewMode,
    isRepositoryLoading: dataRepository.isLoading,
    isWorkspaceAppHostDiscoveryComplete: dataRepository.isWorkspaceAppHostDiscoveryComplete,
    hasError: dataRepository.hasError,
    errorMessage: dataRepository.errorMessage,
    workspaceAppHost: dataRepository.workspaceAppHost ? cloneAppHostState(dataRepository.workspaceAppHost, includeSensitiveDashboardUrls) : undefined,
    workspaceAppHostName: dataRepository.workspaceAppHostName,
    workspaceAppHostPath: dataRepository.workspaceAppHostPath,
    workspaceAppHostCandidatePaths: [...dataRepository.workspaceAppHostCandidatePaths],
    workspaceAppHostDescription: dataRepository.workspaceAppHostDescription,
    workspaceResources: dataRepository.workspaceResources.map(resource => cloneResourceState(resource, includeSensitiveDashboardUrls)),
    appHosts: dataRepository.appHosts.map(appHost => cloneAppHostState(appHost, includeSensitiveDashboardUrls)),
    launchingPaths: [...appHostLaunchService.launchingPaths],
    stoppingPaths: [...appHostTreeProvider.stoppingPaths],
    debugSessions: extensionContext.aspireDebugSessions.map(session => ({
      appHostPath: session.appHostPath,
      dashboardUrl: session.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(session.dashboardUrl) : sanitizeDashboardUrl(session.dashboardUrl),
      startupCompleted: session.startupCompleted,
    })),
  };
}

export function getDashboardUrl(dataRepository: AppHostDataRepository, appHostPath?: string): string | undefined {
  return sanitizeDashboardUrl(getSensitiveDashboardUrl(dataRepository, appHostPath));
}

export function getSensitiveDashboardUrl(dataRepository: AppHostDataRepository, appHostPath?: string): string | undefined {
  if (appHostPath) {
    const matchingAppHost = dataRepository.appHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath));
    return matchingAppHost?.dashboardUrl ? stripResourceSuffix(matchingAppHost.dashboardUrl) : undefined;
  }

  if (dataRepository.workspaceAppHost?.dashboardUrl) {
    return stripResourceSuffix(dataRepository.workspaceAppHost.dashboardUrl);
  }

  const appHostsWithDashboard = dataRepository.appHosts.filter(appHost => appHost.dashboardUrl);
  if (appHostsWithDashboard.length > 1) {
    return undefined;
  }

  const dashboardUrl = appHostsWithDashboard[0]?.dashboardUrl ?? dataRepository.workspaceResources.find(resource => resource.dashboardUrl)?.dashboardUrl;

  return dashboardUrl ? stripResourceSuffix(dashboardUrl) : undefined;
}

export function cloneAppHostState(appHost: AppHostDisplayInfo, includeSensitiveDashboardUrls: boolean): AspireAppHostState {
  return {
    appHostPath: appHost.appHostPath,
    appHostPid: appHost.appHostPid,
    dashboardUrl: appHost.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(appHost.dashboardUrl) : (sanitizeDashboardUrl(appHost.dashboardUrl) ?? null),
    resources: appHost.resources ? appHost.resources.map(resource => cloneResourceState(resource, includeSensitiveDashboardUrls)) : appHost.resources,
  };
}

function cloneResourceState(resource: ResourceJson, includeSensitiveDashboardUrls: boolean): AspireResourceState {
  return {
    name: resource.name,
    displayName: resource.displayName,
    resourceType: resource.resourceType,
    state: resource.state,
    projectPath: resource.properties?.['project.path'] ?? null,
    dashboardUrl: resource.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(resource.dashboardUrl) : (sanitizeDashboardUrl(resource.dashboardUrl) ?? null),
    urls: resource.urls?.map(cloneResourceUrlState) ?? null,
    commands: resource.commands ? cloneResourceCommands(resource.commands) : null,
  };
}

function cloneResourceUrlState(url: AspireResourceUrlState): AspireResourceUrlState {
  return {
    name: url.name,
    displayName: url.displayName,
    url: url.url,
    isInternal: url.isInternal,
  };
}

function cloneResourceCommands(commands: ResourceJson['commands']): Record<string, AspireResourceCommandState> | null {
  if (!commands) {
    return null;
  }

  return Object.fromEntries(Object.entries(commands).map(([name, command]) => [name, {
    displayName: command.displayName,
    description: command.description,
    state: command.state,
    visibility: command.visibility,
  }]));
}

export function stripResourceSuffix(url: string): string {
  const idx = url.indexOf('/?resource=');
  return idx !== -1 ? url.substring(0, idx) : url;
}

export function sanitizeDashboardUrl(url: string | null | undefined): string | undefined {
  if (!url) {
    return undefined;
  }

  try {
    return new URL(stripResourceSuffix(url)).origin;
  }
  catch {
    return undefined;
  }
}

export function isSamePath(left: string, right: string): boolean {
  const normalizedLeft = path.resolve(left);
  const normalizedRight = path.resolve(right);
  return process.platform === 'win32'
    ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
    : normalizedLeft === normalizedRight;
}
