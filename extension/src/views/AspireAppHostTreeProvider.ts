import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import {
    pidDescription,
    dashboardLabel,
    resourcesGroupLabel,
    noCommandsAvailable,
    selectCommandPlaceholder,
    selectDashboardPlaceholder,
    workspaceAppHostLabel,
    resourceCountDescription,
    tooltipType,
    tooltipState,
    tooltipHealth,
    tooltipEndpoints,
    appHostSourceNotFound,
    appHostSourceOpenFailed,
    logFileOpenFailed,
    logFilePathInvalid,
    healthChecksLabel,
    healthCheckDescription,
    resourceDescriptionHealth,
    resourceDescriptionExitCode,
    logFileLabel,
} from '../loc/strings';
import { isLinkableUrl } from '../utils/urlSchemes';
import {
    AppHostDataRepository,
    AppHostDisplayInfo,
    ResourceCommandArgumentInputJson,
    ResourceJson,
    ViewMode,
    shortenPaths,
} from './AppHostDataRepository';
import { collectResourceCommandArguments, ResourceCommandArgumentValue } from './ResourceCommandArguments';
import { createResourceCommandArgumentLoader } from './ResourceCommandArgumentsLoader';

type TreeElement = AppHostItem | EndpointUrlItem | ResourcesGroupItem | ResourceItem | WorkspaceResourcesItem | HealthChecksGroupItem | HealthCheckItem | LogFileItem;

function sortResources(resources: ResourceJson[]): ResourceJson[] {
    return [...resources].sort((a, b) => {
        const nameA = (a.displayName ?? a.name).toLowerCase();
        const nameB = (b.displayName ?? b.name).toLowerCase();
        return nameA.localeCompare(nameB);
    });
}

function isSamePath(left: string, right: string): boolean {
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    return getComparisonKey(resolvedLeft) === getComparisonKey(resolvedRight);
}

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}

function hasNoResources(resources: readonly ResourceJson[] | null | undefined): boolean {
    return resources === undefined || resources === null || resources.length === 0;
}

function appHostIcon(path?: string): vscode.ThemeIcon {
    const icon = path?.endsWith('.csproj') ? 'server-process' : 'file-code';
    return new vscode.ThemeIcon(icon, new vscode.ThemeColor('aspire.brandPurple'));
}

function stripResourceSuffix(url: string): string {
    const idx = url.indexOf('/?resource=');
    return idx !== -1 ? url.substring(0, idx) : url;
}

class AppHostItem extends vscode.TreeItem {
    constructor(public readonly appHost: AppHostDisplayInfo, label: string, appHostDescription?: string) {
        super(label, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `apphost:${appHost.appHostPid}`;
        this.description = pidDescription(appHost.appHostPid);
        this.iconPath = appHostIcon(appHost.appHostPath);
        this.contextValue = 'appHost';
        this.tooltip = appHostDescription ? `${appHostDescription}\n${appHost.appHostPath}` : appHost.appHostPath;
    }
}

class WorkspaceResourcesItem extends vscode.TreeItem {
    constructor(
        public readonly resources: ResourceJson[],
        public readonly dashboardUrl: string | null,
        public readonly appHostPath: string | undefined,
        public readonly appHost: AppHostDisplayInfo | undefined,
        appHostName?: string,
        appHostDescription?: string
    ) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-resources';
        this.iconPath = appHostIcon(appHostPath);
        this.contextValue = appHost ? 'workspaceResources:hasAppHost' : 'workspaceResources';
        this.description = resourceCountDescription(resources.length);
        this.tooltip = appHostDescription;
    }
}

class EndpointUrlItem extends vscode.TreeItem {
    constructor(public readonly url: string, displayName: string) {
        super(displayName, vscode.TreeItemCollapsibleState.None);
        this.tooltip = url;

        const uri = vscode.Uri.parse(url);
        if (isLinkableUrl(url)) {
            this.iconPath = new vscode.ThemeIcon('link-external');
            this.contextValue = 'endpointUrl';
            this.command = {
                command: 'vscode.open',
                title: url,
                arguments: [uri]
            };
        } else {
            this.iconPath = new vscode.ThemeIcon('radio-tower');
            this.contextValue = 'endpointUrlNonHttp';
        }
    }
}

class LogFileItem extends vscode.TreeItem {
    constructor(public readonly logFilePath: string) {
        super(logFileLabel, vscode.TreeItemCollapsibleState.None);
        this.tooltip = logFilePath;
        this.iconPath = new vscode.ThemeIcon('output');
        this.contextValue = 'logFileItem';
        this.command = {
            command: 'aspire-vscode.viewAppHostLogFile',
            title: logFileLabel,
            arguments: [logFilePath]
        };
    }
}

class ResourcesGroupItem extends vscode.TreeItem {
    constructor(public readonly resources: ResourceJson[], public readonly appHostPid: number) {
        super(resourcesGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `resources:${appHostPid}`;
        this.iconPath = new vscode.ThemeIcon('layers', new vscode.ThemeColor('aspire.brandPurple'));
        this.contextValue = 'resourcesGroup';
        this.description = `(${resources.length})`;
    }
}

class HealthChecksGroupItem extends vscode.TreeItem {
    constructor(public readonly resource: ResourceJson, parentId: string) {
        super(healthChecksLabel, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `${parentId}:health-checks`;
        this.iconPath = new vscode.ThemeIcon('heart');
        this.contextValue = 'healthChecksGroup';
        const reports = resource.healthReports;
        if (reports) {
            const total = Object.keys(reports).length;
            const passed = Object.values(reports).filter(r => r.status === 'Healthy').length;
            this.description = `${passed}/${total}`;
        }
    }
}

class HealthCheckItem extends vscode.TreeItem {
    constructor(name: string, status: string | null, description: string | null, parentId: string) {
        super(name, vscode.TreeItemCollapsibleState.None);
        this.id = `${parentId}:health:${name}`;
        const isHealthy = status === 'Healthy';
        const isDegraded = status === 'Degraded';
        this.iconPath = isHealthy
            ? new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'))
            : isDegraded
                ? new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'))
                : new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
        this.description = healthCheckDescription(status ?? 'Unknown');
        if (description) {
            this.tooltip = description;
        }
        this.contextValue = 'healthCheck';
    }
}

function getParentResourceName(resource: ResourceJson): string | null {
    return resource.properties?.['resource.parentName'] ?? null;
}

class ResourceItem extends vscode.TreeItem {
    constructor(public readonly resource: ResourceJson, public readonly appHostPid: number | null, hasChildren: boolean, public readonly allResources?: readonly ResourceJson[]) {
        const label = resource.displayName ?? resource.name;
        const hasUrls = resource.urls && resource.urls.filter(u => !u.isInternal).length > 0;
        const hasHealthReports = resource.healthReports && Object.keys(resource.healthReports).length > 0;
        const hasExpandableContent = hasChildren || hasUrls || hasHealthReports;
        const collapsible = hasChildren
            ? vscode.TreeItemCollapsibleState.Expanded
            : hasExpandableContent ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None;
        super(label, collapsible);
        this.id = appHostPid !== null ? `resource:${appHostPid}:${resource.name}` : `resource:workspace:${resource.name}`;
        this.iconPath = getResourceIcon(resource);
        this.description = buildResourceDescription(resource);
        this.tooltip = buildResourceTooltip(resource);
        this.contextValue = getResourceContextValue(resource);
    }
}

export function getResourceContextValue(resource: ResourceJson): string {
    const commands = resource.commands ? Object.keys(resource.commands) : [];
    const parts = ['resource'];
    if (commands.includes('start') || commands.includes('resource-start')) {
        parts.push('canStart');
    }
    if (commands.includes('stop') || commands.includes('resource-stop')) {
        parts.push('canStop');
    }
    if (commands.includes('restart') || commands.includes('resource-restart')) {
        parts.push('canRestart');
    }
    return parts.join(':');
}

export function getResourceIcon(resource: ResourceJson): vscode.ThemeIcon {
    const state = resource.state;
    const health = resource.healthStatus;
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
            if (resource.stateStyle === StateStyle.Error) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            if (health === HealthStatus.Unhealthy) {
                return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
            }
            if (health === HealthStatus.Degraded || resource.stateStyle === StateStyle.Warning) {
                return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
            }
            return new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'));
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopped:
            if (resource.stateStyle === StateStyle.Error || (resource.exitCode != null && resource.exitCode !== 0)) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            // Use a hollow circle (matches the `$(circle-outline)` codicon shown in the
            // "Stopped" code-lens label) instead of a green check, so a stopped/finished
            // resource is never visually confused with a Running one (both used to render
            // as a green check, just in slightly different greens).
            return new vscode.ThemeIcon('circle-outline', new vscode.ThemeColor('descriptionForeground'));
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
            return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
        case ResourceState.Starting:
        case ResourceState.Stopping:
        case ResourceState.Building:
        case ResourceState.Waiting:
            return new vscode.ThemeIcon('loading~spin');
        case ResourceState.NotStarted:
            return new vscode.ThemeIcon('record', new vscode.ThemeColor('descriptionForeground'));
        default:
            if (state === null || state === undefined) {
                return new vscode.ThemeIcon('record', new vscode.ThemeColor('descriptionForeground'));
            }
            return new vscode.ThemeIcon('circle-filled', new vscode.ThemeColor('aspire.brandPurple'));
    }
}

export function resolveAppHostSourcePath(appHostPath: string, fileExists: (candidate: string) => boolean = fs.existsSync): string {
    if (!appHostPath.toLowerCase().endsWith('.csproj')) {
        return appHostPath;
    }

    const projectDirectory = path.dirname(appHostPath);
    // C# AppHosts are reported as the project file, but the tree action is meant to
    // take the user to the AppHost source code instead of opening project XML.
    const appHostCodePath = path.join(projectDirectory, 'AppHost.cs');
    if (fileExists(appHostCodePath)) {
        return appHostCodePath;
    }

    const fileBasedAppHostCodePath = path.join(projectDirectory, 'apphost.cs');
    if (fileExists(fileBasedAppHostCodePath)) {
        return fileBasedAppHostCodePath;
    }

    // Older/simple AppHosts may still use Program.cs, so prefer that before
    // falling back to the .csproj when no source file can be resolved.
    const programCodePath = path.join(projectDirectory, 'Program.cs');
    if (fileExists(programCodePath)) {
        return programCodePath;
    }

    return appHostPath;
}

export function buildResourceDescription(resource: ResourceJson): string {
    const parts: string[] = [resource.resourceType];
    const state = resource.state;
    if (state) {
        parts.push(state);
    }
    const reports = resource.healthReports;
    const exitCode = resource.exitCode;
    if (reports && Object.keys(reports).length > 0) {
        const total = Object.keys(reports).length;
        const passed = Object.values(reports).filter(r => r.status === 'Healthy').length;
        parts.push(resourceDescriptionHealth(passed, total));
    }
    if (exitCode != null && exitCode !== 0) {
        parts.push(resourceDescriptionExitCode(exitCode));
    }
    return parts.join(' · ');
}

function buildResourceTooltip(resource: ResourceJson): vscode.MarkdownString {
    const md = new vscode.MarkdownString();
    md.appendMarkdown(`**${resource.displayName ?? resource.name}**\n\n`);
    md.appendMarkdown(`${tooltipType(resource.resourceType)}\n\n`);
    if (resource.state) {
        md.appendMarkdown(`${tooltipState(resource.state)}\n\n`);
    }
    if (resource.healthStatus) {
        md.appendMarkdown(`${tooltipHealth(resource.healthStatus)}\n\n`);
        const reports = resource.healthReports;
        if (reports) {
            const entries = Object.entries(reports).sort(([a], [b]) => a.localeCompare(b));
            for (const [name, report] of entries) {
                let icon = '❓';
                if (report.status === HealthStatus.Healthy) {
                    icon = '✅';
                } else if (report.status === HealthStatus.Degraded) {
                    icon = '⚠️';
                } else if (report.status === HealthStatus.Unhealthy) {
                    icon = '❌';
                }
                md.appendMarkdown(`${icon} ${name}: ${report.status ?? 'Unknown'}${report.description ? ` - ${report.description}` : ''}\n\n`);
            }
        }
    }
    const urls = resource.urls?.filter(u => !u.isInternal && typeof u.url === 'string' && isLinkableUrl(u.url)) ?? [];
    if (urls.length > 0) {
        md.appendMarkdown(`**${tooltipEndpoints}**\n\n`);
        for (const url of urls) {
            md.appendMarkdown(`- [${url.displayName ?? url.url}](${url.url})\n`);
        }
    }
    md.isTrusted = { enabledCommands: [] };
    return md;
}

/**
 * Pure tree-view renderer.  All data comes from the AppHostDataRepository;
 * this class handles only tree rendering and resource command execution.
 */
export class AspireAppHostTreeProvider implements vscode.TreeDataProvider<TreeElement>, vscode.TextDocumentContentProvider {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<TreeElement | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private readonly _onDidChangeContent = new vscode.EventEmitter<vscode.Uri>();
    readonly onDidChange = this._onDidChangeContent.event;

    private readonly _dataSubscription: vscode.Disposable;
    private _contentProviderRegistration: vscode.Disposable | undefined;
    private readonly _appHostSourceContents = new Map<string, string>();
    private _treeView: vscode.TreeView<TreeElement> | undefined;

    private _documentCloseSubscription: vscode.Disposable | undefined;

    constructor(
        private readonly _repository: AppHostDataRepository,
        private readonly _terminalProvider: AspireTerminalProvider,
        private readonly _secretWarningState?: vscode.Memento,
    ) {
        this._dataSubscription = this._repository.onDidChangeData(() => {
            this._onDidChangeTreeData.fire();
        });
    }

    provideTextDocumentContent(uri: vscode.Uri): string {
        return this._appHostSourceContents.get(uri.toString()) ?? '';
    }

    private _ensureContentProviderRegistered(): void {
        if (this._contentProviderRegistration) {
            return;
        }

        this._contentProviderRegistration = vscode.workspace.registerTextDocumentContentProvider('aspire-source', this);
        this._documentCloseSubscription = vscode.workspace.onDidCloseTextDocument(doc => {
            if (doc.uri.scheme === 'aspire-source') {
                this._appHostSourceContents.delete(doc.uri.toString());
            }
        });
    }

    get appHosts(): readonly AppHostDisplayInfo[] {
        return this._repository.appHosts;
    }

    get workspaceResources(): readonly ResourceJson[] {
        return this._repository.workspaceResources;
    }

    get workspaceAppHost(): AppHostDisplayInfo | undefined {
        return this._repository.workspaceAppHost;
    }

    get workspaceAppHostPath(): string | undefined {
        return this._repository.workspaceAppHostPath;
    }

    get viewMode(): ViewMode {
        return this._repository.viewMode;
    }

    dispose(): void {
        this._dataSubscription.dispose();
        this._contentProviderRegistration?.dispose();
        this._documentCloseSubscription?.dispose();
        this._onDidChangeTreeData.dispose();
        this._onDidChangeContent.dispose();
    }

    setTreeView(treeView: vscode.TreeView<TreeElement>): void {
        this._treeView = treeView;
    }

    findResourceElement(resourceName: string, appHostPath?: string): TreeElement | undefined {
        const allChildren = this.getChildren();
        if (appHostPath) {
            const appHostElement = this.findAppHostElement(appHostPath);
            return appHostElement ? this._findResourceInTree([appHostElement], resourceName) : undefined;
        }

        return this._findResourceInTree(allChildren, resourceName);
    }

    /**
     * Finds the {@link AppHostItem} (global mode) or {@link WorkspaceResourcesItem}
     * (workspace mode) that corresponds to the given AppHost path.
     *
     * Matching prefers an exact path match, then falls back to same-directory match,
     * which is needed because C# AppHost paths point at the `.csproj` file while a
     * code lens lives in the sibling `.cs` source.
     */
    findAppHostElement(appHostPath: string): TreeElement | undefined {
        if (!appHostPath) {
            return undefined;
        }
        const targetDir = path.dirname(appHostPath);
        const elements = this.getChildren();
        for (const element of elements) {
            if (element instanceof AppHostItem) {
                const hostPath = element.appHost.appHostPath;
                if (!hostPath) {
                    continue;
                }
                if (isSamePath(hostPath, appHostPath) || isSamePath(path.dirname(hostPath), targetDir)) {
                    return element;
                }
            } else if (element instanceof WorkspaceResourcesItem) {
                const hostPath = element.appHostPath;
                if (!hostPath) {
                    continue;
                }
                if (isSamePath(hostPath, appHostPath) || isSamePath(path.dirname(hostPath), targetDir)) {
                    return element;
                }
            }
        }
        return undefined;
    }

    private _findResourceInTree(elements: TreeElement[], resourceName: string): TreeElement | undefined {
        for (const element of elements) {
            if (element instanceof ResourceItem) {
                const name = element.resource.displayName ?? element.resource.name;
                if (name === resourceName) {
                    return element;
                }
            }
            const children = this.getChildren(element);
            if (children.length > 0) {
                const found = this._findResourceInTree(children, resourceName);
                if (found) {
                    return found;
                }
            }
        }
        return undefined;
    }

    getParent(element: TreeElement): TreeElement | undefined {
        // Resolve ancestry so TreeView.reveal() can expand the correct path.
        return this._findParent(this.getChildren(), element);
    }

    private _findParent(siblings: TreeElement[], target: TreeElement): TreeElement | undefined {
        for (const sibling of siblings) {
            const children = this.getChildren(sibling);
            for (const child of children) {
                if (child.id === target.id) {
                    return sibling;
                }
            }
            const deeper = this._findParent(children, target);
            if (deeper) {
                return deeper;
            }
        }
        return undefined;
    }

    getTreeItem(element: TreeElement): vscode.TreeItem {
        return element;
    }

    getChildren(element?: TreeElement): TreeElement[] {
        if (this._repository.viewMode === 'workspace') {
            return this._getWorkspaceChildren(element);
        }
        return this._getGlobalChildren(element);
    }

    // ── Workspace mode tree ──

    private _getWorkspaceChildren(element?: TreeElement): TreeElement[] {
        if (!element) {
            const workspaceResources = [...this._repository.workspaceResources];
            const workspaceAppHost = this._repository.workspaceAppHost;
            if (this._repository.hasMultipleWorkspaceAppHosts && this._repository.appHosts.length > 0) {
                const appHosts = this._repository.appHosts.map(appHost => {
                    const selectedAppHostPath = workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath;
                    if (workspaceResources.length > 0 && selectedAppHostPath && isSamePath(appHost.appHostPath, selectedAppHostPath) && hasNoResources(appHost.resources)) {
                        return { ...appHost, resources: workspaceResources };
                    }

                    return appHost;
                });
                const labels = shortenPaths(appHosts.map(appHost => appHost.appHostPath));
                return appHosts.map((appHost, index) => new AppHostItem(appHost, labels[index], this._repository.workspaceAppHostDescription));
            }
            if (workspaceResources.length === 0 && !workspaceAppHost) {
                return [];
            }
            const resources = workspaceResources.length > 0
                ? workspaceResources
                : [...workspaceAppHost?.resources ?? []];
            const rawDashboardUrl = workspaceAppHost?.dashboardUrl ?? resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
            const dashboardUrl = rawDashboardUrl ? stripResourceSuffix(rawDashboardUrl) : null;
            return [new WorkspaceResourcesItem(resources, dashboardUrl, workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath, workspaceAppHost, this._repository.workspaceAppHostName, this._repository.workspaceAppHostDescription)];
        }

        if (element instanceof AppHostItem || element instanceof ResourcesGroupItem) {
            return this._getGlobalChildren(element);
        }

        if (element instanceof WorkspaceResourcesItem) {
            const items: TreeElement[] = [];

            if (element.dashboardUrl) {
                items.push(new EndpointUrlItem(element.dashboardUrl, dashboardLabel));
            }

            if (element.appHost?.logFilePath) {
                items.push(new LogFileItem(element.appHost.logFilePath));
            }

            // Show only top-level resources (those without a parent)
            const topLevel = element.resources.filter(r => !getParentResourceName(r));
            for (const resource of sortResources(topLevel)) {
                const hasChildren = element.resources.some(r => getParentResourceName(r) === resource.name);
                items.push(new ResourceItem(resource, null, hasChildren, element.resources));
            }
            return items;
        }

        if (element instanceof ResourceItem) {
            const appHost = element.appHostPid !== null
                ? this._repository.appHosts.find(a => a.appHostPid === element.appHostPid)
                : undefined;
            const workspaceResources = [...this._repository.workspaceResources];
            const selectedAppHostPath = this._repository.workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath;
            const allResources = element.allResources ?? (appHost && workspaceResources.length > 0 && selectedAppHostPath && isSamePath(appHost.appHostPath, selectedAppHostPath) && hasNoResources(appHost.resources)
                ? workspaceResources
                : appHost?.resources ?? workspaceResources);
            return this._getResourceChildren(element, allResources);
        }

        if (element instanceof HealthChecksGroupItem) {
            return this._getHealthCheckChildren(element);
        }

        return [];
    }

    // ── Global mode tree ──

    private _getGlobalChildren(element?: TreeElement): TreeElement[] {
        if (!element) {
            const appHosts = this._repository.appHosts;
            const labels = shortenPaths(appHosts.map(appHost => appHost.appHostPath));
            return appHosts.map((appHost, index) => new AppHostItem(appHost, labels[index], this._repository.workspaceAppHostDescription));
        }

        if (element instanceof AppHostItem) {
            const items: (EndpointUrlItem | ResourcesGroupItem | LogFileItem)[] = [];
            const appHost = element.appHost;

            if (appHost.dashboardUrl) {
                items.push(new EndpointUrlItem(appHost.dashboardUrl, dashboardLabel));
            }

            if (appHost.logFilePath) {
                items.push(new LogFileItem(appHost.logFilePath));
            }

            if (appHost.resources && appHost.resources.length > 0) {
                items.push(new ResourcesGroupItem(appHost.resources, appHost.appHostPid));
            }

            return items;
        }

        if (element instanceof ResourcesGroupItem) {
            const topLevel = element.resources.filter(r => !getParentResourceName(r));
            return sortResources(topLevel).map(r => {
                const hasChildren = element.resources.some(c => getParentResourceName(c) === r.name);
                return new ResourceItem(r, element.appHostPid, hasChildren, element.resources);
            });
        }

        if (element instanceof ResourceItem) {
            const allResources = this._repository.viewMode === 'workspace'
                ? [...this._repository.workspaceResources]
                : this._repository.appHosts.find(a => a.appHostPid === element.appHostPid)?.resources ?? [];
            return this._getResourceChildren(element, allResources);
        }
        if (element instanceof HealthChecksGroupItem) {
            return this._getHealthCheckChildren(element);
        }

        return [];
    }

    private _getResourceChildren(element: ResourceItem, allResources: readonly ResourceJson[]): TreeElement[] {
        const items: TreeElement[] = [];

        const children = allResources.filter(r => getParentResourceName(r) === element.resource.name);
        for (const child of sortResources(children)) {
            const hasChildren = allResources.some(r => getParentResourceName(r) === child.name);
            items.push(new ResourceItem(child, element.appHostPid, hasChildren, allResources));
        }

        const urls = element.resource.urls?.filter(u => !u.isInternal) ?? [];
        items.push(...urls.map(url => new EndpointUrlItem(url.url, url.displayName ?? url.url)));

        const reports = element.resource.healthReports;
        if (reports && Object.keys(reports).length > 0) {
            items.push(new HealthChecksGroupItem(element.resource, element.id!));
        }

        return items;
    }

    private _getHealthCheckChildren(element: HealthChecksGroupItem): TreeElement[] {
        const reports = element.resource.healthReports;
        if (!reports) {
            return [];
        }
        return Object.entries(reports)
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([name, report]) => new HealthCheckItem(name, report.status, report.description, element.id!));
    }

    // ── Commands ──

    async expandAll(element?: TreeElement): Promise<void> {
        if (!this._treeView || !element) {
            return;
        }
        const children = this.getChildren(element);
        for (const child of children) {
            if (child.collapsibleState !== vscode.TreeItemCollapsibleState.None) {
                await this._treeView.reveal(child, { expand: 3 });
            }
        }
    }

    async openDashboard(element?: TreeElement): Promise<void> {
        let url: string | null = null;

        if (element instanceof AppHostItem) {
            url = element.appHost.dashboardUrl;
        }

        if (element instanceof WorkspaceResourcesItem) {
            url = getBaseDashboardUrl(element.dashboardUrl);
        }

        if (!url) {
            if (this._repository.viewMode === 'workspace') {
                const resources = [...this._repository.workspaceResources];
                const resourceUrl = this._repository.workspaceAppHost?.dashboardUrl ?? resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
                url = getBaseDashboardUrl(resourceUrl);
            } else {
                const appHosts = this._repository.appHosts.filter(a => a.dashboardUrl);
                if (appHosts.length === 1) {
                    url = appHosts[0].dashboardUrl;
                } else if (appHosts.length > 1) {
                    const labels = shortenPaths(appHosts.map(a => a.appHostPath));
                    const items = appHosts.map((a, index) => ({
                        label: labels[index],
                        description: pidDescription(a.appHostPid),
                        dashboardUrl: a.dashboardUrl!,
                    }));
                    const selected = await vscode.window.showQuickPick(items, {
                        placeHolder: selectDashboardPlaceholder,
                    });
                    if (!selected) {
                        return;
                    }
                    url = selected.dashboardUrl;
                }
            }
        }

        if (url) {
            vscode.env.openExternal(vscode.Uri.parse(url));
        }
    }

    stopAppHost(element: AppHostItem): void {
        this._terminalProvider.sendAspireCommandToAspireTerminal(`stop --apphost "${element.appHost.appHostPath}"`);
    }

    async openAppHostSource(element?: AppHostItem | WorkspaceResourcesItem): Promise<void> {
        if (!element || !(element instanceof AppHostItem || element instanceof WorkspaceResourcesItem)) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }

        const appHostPath = element instanceof AppHostItem
            ? element.appHost.appHostPath
            : element.appHostPath;

        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }

        const sourcePath = resolveAppHostSourcePath(appHostPath);
        try {
            // Open the resolved source path directly so TypeScript AppHosts open their
            // file as-is, while C# AppHosts route through the .csproj special case above.
            const document = await vscode.workspace.openTextDocument(vscode.Uri.file(sourcePath));
            await vscode.window.showTextDocument(document, { preview: false });
        } catch {
            vscode.window.showWarningMessage(appHostSourceOpenFailed(sourcePath));
        }
    }

    stopResource(element: ResourceItem): void {
        this._runResourceCommand(element, 'stop');
    }

    startResource(element: ResourceItem): void {
        this._runResourceCommand(element, 'start');
    }

    restartResource(element: ResourceItem): void {
        this._runResourceCommand(element, 'restart');
    }

    viewResourceLogs(element: ResourceItem): void {
        // aspire logs accepts the resource display name, not the internal name
        const resourceName = element.resource.displayName ?? element.resource.name;
        if (this._repository.viewMode === 'workspace') {
            const appHostPath = this._getAppHostPathForResource(element);
            const appHostFlag = appHostPath ? ` --apphost "${appHostPath}"` : '';
            this._terminalProvider.sendAspireCommandToAspireTerminal(`logs "${resourceName}"${appHostFlag}`);
            return;
        }
        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        this._terminalProvider.sendAspireCommandToAspireTerminal(`logs "${resourceName}" --apphost "${appHost.appHostPath}"`);
    }

    async executeResourceCommand(element: ResourceItem): Promise<void> {
        const commands = element.resource.commands;
        if (!commands || Object.keys(commands).length === 0) {
            vscode.window.showInformationMessage(noCommandsAvailable);
            return;
        }

        const items = Object.entries(commands).map(([name, cmd]) => ({
            label: name,
            description: cmd.description ?? undefined,
            command: cmd,
        }));

        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: selectCommandPlaceholder,
        });

        if (!selected) {
            return;
        }

        const commandArguments = await collectResourceCommandArguments(selected.label, selected.command, {
            secretWarningState: this._secretWarningState,
            loadDynamicArguments: values => this._loadResourceCommandArguments(element, selected.label, values),
        });
        if (commandArguments === undefined) {
            return;
        }

        this._runResourceCommand(element, `"${selected.label}"`, commandArguments.args, commandArguments.containsSecret);
    }

    async copyAppHostPath(element: AppHostItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.appHost.appHostPath);
    }

    async viewAppHostLogFile(element: unknown): Promise<void> {
        const filePath = element instanceof LogFileItem ? element.logFilePath : element as string;
        if (!filePath || typeof filePath !== 'string') {
            vscode.window.showWarningMessage(logFilePathInvalid);
            return;
        }
        try {
            const uri = vscode.Uri.file(filePath);
            const document = await vscode.workspace.openTextDocument(uri);
            await vscode.window.showTextDocument(document, { preview: false });
        } catch (error) {
            vscode.window.showWarningMessage(logFileOpenFailed(filePath, getErrorMessage(error)));
        }
    }

    async copyLogFilePath(element: LogFileItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.logFilePath);
    }

    async copyEndpointUrl(element: EndpointUrlItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.url);
    }

    async copyResourceName(element: ResourceItem): Promise<void> {
        const name = element.resource.displayName ?? element.resource.name;
        await vscode.env.clipboard.writeText(name);
    }

    async viewAppHostSource(element?: AppHostItem | WorkspaceResourcesItem): Promise<void> {
        let appHost: AppHostDisplayInfo | undefined;
        if (element instanceof AppHostItem) {
            appHost = element.appHost;
        } else if (element instanceof WorkspaceResourcesItem) {
            appHost = element.appHost;
        }
        if (!appHost) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }
        const json = JSON.stringify(appHost, null, 2);
        const uri = vscode.Uri.parse(`aspire-source:AppHost-${appHost.appHostPid}.json`);
        this._ensureContentProviderRegistered();
        this._appHostSourceContents.set(uri.toString(), json);
        this._onDidChangeContent.fire(uri);
        const document = await vscode.workspace.openTextDocument(uri);
        await vscode.window.showTextDocument(document, { preview: true });
    }

    openInExternalBrowser(element: EndpointUrlItem): void {
        vscode.env.openExternal(vscode.Uri.parse(element.url));
    }

    openInIntegratedBrowser(element: EndpointUrlItem): void {
        vscode.commands.executeCommand('simpleBrowser.show', element.url);
    }

    private _runResourceCommand(element: ResourceItem, command: string, additionalArgs?: string[], redactAdditionalArgs = false): void {
        if (this._repository.viewMode === 'workspace') {
            const appHostPath = this._getAppHostPathForResource(element);
            const appHostFlag = appHostPath ? ` --apphost "${appHostPath}"` : '';
            this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" ${command}${appHostFlag}`, true, additionalArgs, { redactAdditionalArgs });
            return;
        }

        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" ${command} --apphost "${appHost.appHostPath}"`, true, additionalArgs, { redactAdditionalArgs });
    }

    private async _loadResourceCommandArguments(element: ResourceItem, commandName: string, values: readonly ResourceCommandArgumentValue[]): Promise<ResourceCommandArgumentInputJson[] | undefined> {
        const appHostPath = this._repository.viewMode === 'workspace'
            ? this._repository.workspaceAppHostPath
            : this._findAppHostForResource(element)?.appHostPath;

        const loader = createResourceCommandArgumentLoader({
            cliExecutionProvider: this._terminalProvider,
            resourceName: element.resource.name,
            commandName,
            appHostPath: appHostPath ?? undefined,
        });

        return await loader(values);
    }

    private _findAppHostForResource(element: ResourceItem): AppHostDisplayInfo | undefined {
        return this._repository.appHosts.find(a => a.appHostPid === element.appHostPid);
    }

    private _getAppHostPathForResource(element: ResourceItem): string | undefined {
        return this._findAppHostForResource(element)?.appHostPath ?? this._repository.workspaceAppHostPath;
    }
}

/**
 * Strips the resource-specific path suffix from a resource dashboard URL
 * to return the base dashboard URL.
 *
 * Resource dashboard URLs are constructed by appending `/?resource=name` to the
 * base URL (e.g. `http://localhost:18888/login?t=token/?resource=myservice`).
 */
function getBaseDashboardUrl(resourceDashboardUrl: string | null): string | null {
    if (!resourceDashboardUrl) {
        return null;
    }
    const idx = resourceDashboardUrl.indexOf('/?resource=');
    return idx >= 0 ? resourceDashboardUrl.substring(0, idx) : resourceDashboardUrl;
}

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
