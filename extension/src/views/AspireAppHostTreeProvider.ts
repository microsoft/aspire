import * as vscode from 'vscode';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import {
    pidDescription,
    dashboardLabel,
    cliPidLabel,
    appHostPidLabel,
    resourcesGroupLabel,
    resourceStateLabel,
    noCommandsAvailable,
    selectCommandPlaceholder,
    selectDashboardPlaceholder,
    workspaceAppHostLabel,
    resourceCountDescription,
    tooltipType,
    tooltipState,
    tooltipHealth,
    tooltipEndpoints,
} from '../loc/strings';
import {
    AppHostDataRepository,
    AppHostDisplayInfo,
    ResourceJson,
    ViewMode,
    shortenPath,
} from './AppHostDataRepository';

type TreeElement = AppHostItem | PidItem | EndpointUrlItem | ResourcesGroupItem | ResourceItem | WorkspaceResourcesItem;

function sortResources(resources: ResourceJson[]): ResourceJson[] {
    return [...resources].sort((a, b) => {
        const nameA = (a.displayName ?? a.name).toLowerCase();
        const nameB = (b.displayName ?? b.name).toLowerCase();
        return nameA.localeCompare(nameB);
    });
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
    constructor(public readonly appHost: AppHostDisplayInfo) {
        const name = shortenPath(appHost.appHostPath);
        super(name, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `apphost:${appHost.appHostPid}`;
        this.description = pidDescription(appHost.appHostPid);
        this.iconPath = appHostIcon(appHost.appHostPath);
        this.contextValue = 'appHost';
        this.tooltip = appHost.appHostPath;
    }
}

class WorkspaceResourcesItem extends vscode.TreeItem {
    constructor(public readonly resources: ResourceJson[], public readonly dashboardUrl: string | null, appHostPath?: string, appHostName?: string) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-resources';
        this.iconPath = appHostIcon(appHostPath);
        this.contextValue = 'workspaceResources';
        this.description = resourceCountDescription(resources.length);
    }
}

class PidItem extends vscode.TreeItem {
    constructor(public readonly pid: number, label: string, icon: string) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.iconPath = new vscode.ThemeIcon(icon);
        this.contextValue = 'pidItem';
    }
}

class EndpointUrlItem extends vscode.TreeItem {
    constructor(public readonly url: string, displayName: string) {
        super(displayName, vscode.TreeItemCollapsibleState.None);
        this.tooltip = url;

        const uri = vscode.Uri.parse(url);
        const isHttpUrl = uri.scheme.toLowerCase() === 'http' || uri.scheme.toLowerCase() === 'https';
        if (isHttpUrl) {
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

class ResourcesGroupItem extends vscode.TreeItem {
    constructor(public readonly resources: ResourceJson[], public readonly appHostPid: number) {
        super(resourcesGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `resources:${appHostPid}`;
        this.iconPath = new vscode.ThemeIcon('layers', new vscode.ThemeColor('aspire.brandPurple'));
        this.contextValue = 'resourcesGroup';
        this.description = `(${resources.length})`;
    }
}

function getParentResourceName(resource: ResourceJson): string | null {
    return resource.properties?.['resource.parentName'] ?? null;
}

class ResourceItem extends vscode.TreeItem {
    constructor(public readonly resource: ResourceJson, public readonly appHostPid: number | null, hasChildren: boolean) {
        const state = resource.state ?? '';
        const label = state ? resourceStateLabel(resource.displayName ?? resource.name, state) : (resource.displayName ?? resource.name);
        const hasUrls = resource.urls && resource.urls.filter(u => !u.isInternal).length > 0;
        const collapsible = hasChildren
            ? vscode.TreeItemCollapsibleState.Expanded
            : hasUrls ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None;
        super(label, collapsible);
        this.id = appHostPid !== null ? `resource:${appHostPid}:${resource.name}` : `resource:workspace:${resource.name}`;
        this.iconPath = getResourceIcon(resource);
        this.description = resource.resourceType;
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
            if (health === HealthStatus.Unhealthy || resource.stateStyle === StateStyle.Error) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            if (health === HealthStatus.Degraded || resource.stateStyle === StateStyle.Warning) {
                return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
            }
            return new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'));
        case ResourceState.Finished:
        case ResourceState.Exited:
            if (resource.stateStyle === StateStyle.Error) {
                return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
            }
            return new vscode.ThemeIcon('circle-outline');
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
            return new vscode.ThemeIcon('error', new vscode.ThemeColor('list.errorForeground'));
        case ResourceState.Starting:
        case ResourceState.Stopping:
        case ResourceState.Building:
        case ResourceState.Waiting:
        case ResourceState.NotStarted:
            return new vscode.ThemeIcon('loading~spin');
        default:
            if (state === null || state === undefined) {
                return new vscode.ThemeIcon('circle-outline');
            }
            return new vscode.ThemeIcon('circle-filled', new vscode.ThemeColor('aspire.brandPurple'));
    }
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
    }
    const urls = resource.urls?.filter(u => !u.isInternal && typeof u.url === 'string' && (u.url.startsWith('http://') || u.url.startsWith('https://'))) ?? [];
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
export class AspireAppHostTreeProvider implements vscode.TreeDataProvider<TreeElement> {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<TreeElement | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private readonly _dataSubscription: vscode.Disposable;

    constructor(
        private readonly _repository: AppHostDataRepository,
        private readonly _terminalProvider: AspireTerminalProvider,
    ) {
        this._dataSubscription = this._repository.onDidChangeData(() => {
            this._onDidChangeTreeData.fire();
        });
    }

    get appHosts(): readonly AppHostDisplayInfo[] {
        return this._repository.appHosts;
    }

    get workspaceResources(): readonly ResourceJson[] {
        return this._repository.workspaceResources;
    }

    get workspaceAppHostPath(): string | undefined {
        return this._repository.workspaceAppHostPath;
    }

    get viewMode(): ViewMode {
        return this._repository.viewMode;
    }

    dispose(): void {
        this._dataSubscription.dispose();
        this._onDidChangeTreeData.dispose();
    }

    findResourceElement(resourceName: string): TreeElement | undefined {
        const allChildren = this.getChildren();
        return this._findResourceInTree(allChildren, resourceName);
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
            const resources = [...this._repository.workspaceResources];
            if (resources.length === 0) {
                return [];
            }
            const rawDashboardUrl = resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
            const dashboardUrl = rawDashboardUrl ? stripResourceSuffix(rawDashboardUrl) : null;
            return [new WorkspaceResourcesItem(resources, dashboardUrl, this._repository.workspaceAppHostPath, this._repository.workspaceAppHostName)];
        }

        if (element instanceof WorkspaceResourcesItem) {
            const items: TreeElement[] = [];

            if (element.dashboardUrl) {
                items.push(new EndpointUrlItem(element.dashboardUrl, dashboardLabel));
            }

            // Show only top-level resources (those without a parent)
            const topLevel = element.resources.filter(r => !getParentResourceName(r));
            for (const resource of sortResources(topLevel)) {
                const hasChildren = element.resources.some(r => getParentResourceName(r) === resource.name);
                items.push(new ResourceItem(resource, null, hasChildren));
            }
            return items;
        }

        if (element instanceof ResourceItem) {
            return this._getResourceChildren(element, [...this._repository.workspaceResources]);
        }

        return [];
    }

    // ── Global mode tree ──

    private _getGlobalChildren(element?: TreeElement): TreeElement[] {
        if (!element) {
            return this._repository.appHosts.map(appHost => new AppHostItem(appHost));
        }

        if (element instanceof AppHostItem) {
            const items: (PidItem | EndpointUrlItem | ResourcesGroupItem)[] = [];
            const appHost = element.appHost;

            if (appHost.dashboardUrl) {
                items.push(new EndpointUrlItem(appHost.dashboardUrl, dashboardLabel));
            }

            items.push(new PidItem(
                appHost.appHostPid,
                appHostPidLabel(appHost.appHostPid),
                'terminal',
            ));

            if (appHost.cliPid !== null) {
                items.push(new PidItem(
                    appHost.cliPid,
                    cliPidLabel(appHost.cliPid),
                    'terminal-cmd',
                ));
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
                return new ResourceItem(r, element.appHostPid, hasChildren);
            });
        }

        if (element instanceof ResourceItem) {
            const allResources = this._repository.viewMode === 'workspace'
                ? [...this._repository.workspaceResources]
                : this._repository.appHosts.find(a => a.appHostPid === element.appHostPid)?.resources ?? [];
            return this._getResourceChildren(element, allResources);
        }

        return [];
    }

    private _getResourceChildren(element: ResourceItem, allResources: readonly ResourceJson[]): TreeElement[] {
        const items: TreeElement[] = [];

        const children = allResources.filter(r => getParentResourceName(r) === element.resource.name);
        for (const child of sortResources(children)) {
            const hasChildren = allResources.some(r => getParentResourceName(r) === child.name);
            items.push(new ResourceItem(child, element.appHostPid, hasChildren));
        }

        const urls = element.resource.urls?.filter(u => !u.isInternal) ?? [];
        items.push(...urls.map(url => new EndpointUrlItem(url.url, url.displayName ?? url.url)));

        return items;
    }

    // ── Commands ──

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
                const resourceUrl = resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
                url = getBaseDashboardUrl(resourceUrl);
            } else {
                const appHosts = this._repository.appHosts.filter(a => a.dashboardUrl);
                if (appHosts.length === 1) {
                    url = appHosts[0].dashboardUrl;
                } else if (appHosts.length > 1) {
                    const items = appHosts.map(a => ({
                        label: shortenPath(a.appHostPath),
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
            const appHostFlag = this._repository.workspaceAppHostPath ? ` --apphost "${this._repository.workspaceAppHostPath}"` : '';
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
        }));

        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: selectCommandPlaceholder,
        });

        if (!selected) {
            return;
        }

        if (this._repository.viewMode === 'workspace') {
            const appHostFlag = this._repository.workspaceAppHostPath ? ` --apphost "${this._repository.workspaceAppHostPath}"` : '';
            this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" "${selected.label}"${appHostFlag}`);
            return;
        }

        const appHost = this._findAppHostForResource(element);
        if (appHost) {
            this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" "${selected.label}" --apphost "${appHost.appHostPath}"`);
        }
    }

    async copyAppHostPath(element: AppHostItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.appHost.appHostPath);
    }

    async copyEndpointUrl(element: EndpointUrlItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.url);
    }

    async copyResourceName(element: ResourceItem): Promise<void> {
        const name = element.resource.displayName ?? element.resource.name;
        await vscode.env.clipboard.writeText(name);
    }

    async copyPid(element: PidItem): Promise<void> {
        await vscode.env.clipboard.writeText(element.pid.toString());
    }

    openInExternalBrowser(element: EndpointUrlItem): void {
        vscode.env.openExternal(vscode.Uri.parse(element.url));
    }

    openInSimpleBrowser(element: EndpointUrlItem): void {
        vscode.commands.executeCommand('simpleBrowser.show', element.url);
    }

    private _runResourceCommand(element: ResourceItem, command: string, ...extraArgs: string[]): void {
        const suffix = extraArgs.length > 0 ? ` ${extraArgs.join(' ')}` : '';

        if (this._repository.viewMode === 'workspace') {
            const appHostFlag = this._repository.workspaceAppHostPath ? ` --apphost "${this._repository.workspaceAppHostPath}"` : '';
            this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" ${command}${suffix}${appHostFlag}`);
            return;
        }

        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        this._terminalProvider.sendAspireCommandToAspireTerminal(`resource "${element.resource.name}" ${command} --apphost "${appHost.appHostPath}"${suffix}`);
    }

    private _findAppHostForResource(element: ResourceItem): AppHostDisplayInfo | undefined {
        return this._repository.appHosts.find(a => a.appHostPid === element.appHostPid);
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
