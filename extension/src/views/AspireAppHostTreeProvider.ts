import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { AspireTerminalProvider, shellArg } from '../utils/AspireTerminalProvider';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';
import { compareResourceCommands, getParameterValueDescription, getResourceStateDescription } from '../utils/resourceDisplay';
import {
    pidDescription,
    dashboardLabel,
    resourcesGroupLabel,
    noCommandsAvailable,
    selectCommandPlaceholder,
    selectDashboardPlaceholder,
    workspaceAppHostLabel,
    workspaceAppHostsGroupLabel,
    runningAppHostsGroupLabel,
    appHostOpenSourceActionLabel,
    appHostRunActionLabel,
    appHostDebugActionLabel,
    appHostPathLabel,
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
    commandsLabel,
    resourceCommandDisabledDescription,
    appHostStartingDescription,
    appHostStoppingDescription,
    dashboardUrlNotFound,
    dashboardUrlUnsupported,
    errorMessage,
} from '../loc/strings';
import { isLinkableUrl } from '../utils/urlSchemes';
import {
    AppHostDataRepository,
    AppHostDisplayInfo,
    ResourceCommandArgumentInputJson,
    ResourceJson,
    ViewMode,
    isMatchingAppHostPath,
    shortenPaths,
    ResourceCommandJson,
} from './AppHostDataRepository';
import { collectResourceCommandArguments, ResourceCommandArgumentValue } from './ResourceCommandArguments';
import { createResourceCommandArgumentLoader } from './ResourceCommandArgumentsLoader';
import { AppHostLaunchService } from '../services/AppHostLaunchService';

type TreeElement = AppHostItem | EndpointUrlItem | ResourcesGroupItem | ResourceItem | WorkspaceResourcesItem | WorkspaceAppHostItem | WorkspaceAppHostsGroupItem | RunningAppHostsGroupItem | WorkspaceAppHostActionItem | WorkspaceAppHostPathItem | HealthChecksGroupItem | HealthCheckItem | LogFileItem | CommandsGroupItem | ResourceCommandItem;

const integratedBrowserOpenCommand = 'workbench.action.browser.open';

function sortResources(resources: ResourceJson[]): ResourceJson[] {
    return [...resources].sort((a, b) => {
        const nameA = (a.displayName ?? a.name).toLowerCase();
        const nameB = (b.displayName ?? b.name).toLowerCase();
        return nameA.localeCompare(nameB);
    });
}

function getVisibleResourceUrls(resource: ResourceJson) {
    return resource.urls?.filter(u => !u.isInternal && typeof u.url === 'string') ?? [];
}

function getLinkableResourceUrls(resource: ResourceJson) {
    return getVisibleResourceUrls(resource).filter(u => isLinkableUrl(u.url));
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

function getVisibleCommands(commands: Record<string, ResourceCommandJson>): [string, ResourceCommandJson][] {
    return Object.entries(commands)
        .filter(([, command]) => isCommandVisibleToUi(command) && (isEnabledCommand(command) || command.state === 'Disabled'))
        .sort(compareResourceCommands);
}

export function isEnabledCommand(command: ResourceCommandJson | null | undefined): boolean {
    return command !== null && command !== undefined
        && (command.state === undefined || command.state === null || command.state === 'Enabled');
}

export function isCommandVisibleToUi(command: ResourceCommandJson | null | undefined): boolean {
    const visibility = command?.visibility;
    if (visibility === undefined || visibility === null || visibility.trim().length === 0) {
        return true;
    }

    return visibility.split(',')
        .some(value => value.trim().toLowerCase() === 'ui');
}

/**
 * Maps a resource command to a Codicon. The CLI command JSON does not carry the dashboard's Fluent
 * icon name, so we can't reuse the per-command icons shown in the dashboard. Instead we map the
 * well-known lifecycle command names to distinct Codicons so they aren't all rendered with the same
 * glyph, and fall back to a generic "run" icon for custom commands. Command names can be emitted
 * either bare (`start`) or with a `resource-` prefix (`resource-start`) depending on the source, so
 * we match on the suffix.
 *
 * Some Codicons (e.g. `play`, `debug-stop`) carry intrinsic green/red theming that is visually noisy
 * in a dense tree, so we force a neutral foreground color for enabled commands and the standard
 * disabled foreground for disabled ones.
 */
export function getResourceCommandIcon(commandName: string, isEnabled: boolean): vscode.ThemeIcon {
    const color = new vscode.ThemeColor(isEnabled ? 'icon.foreground' : 'disabledForeground');
    const normalized = commandName.replace(/^resource-/, '');
    switch (normalized) {
        case 'start':
            return new vscode.ThemeIcon('play', color);
        case 'stop':
            return new vscode.ThemeIcon('debug-stop', color);
        case 'restart':
            return new vscode.ThemeIcon('debug-restart', color);
        case 'rebuild':
            return new vscode.ThemeIcon('tools', color);
        default:
            return new vscode.ThemeIcon('run', color);
    }
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
    constructor(public readonly appHost: AppHostDisplayInfo, label: string, appHostDescription?: string, stopping = false) {
        super(label, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `apphost:${appHost.appHostPid}`;
        this.description = stopping ? appHostStoppingDescription : pidDescription(appHost.appHostPid);
        this.iconPath = stopping ? new vscode.ThemeIcon('loading~spin') : appHostIcon(appHost.appHostPath);
        this.contextValue = stopping ? 'appHost:stopping' : 'appHost';
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
        appHostDescription?: string,
        stopping = false
    ) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-resources';
        this.iconPath = stopping ? new vscode.ThemeIcon('loading~spin') : appHostIcon(appHostPath);
        this.contextValue = stopping ? 'workspaceResources:stopping' : appHost ? 'workspaceResources:hasAppHost' : 'workspaceResources';
        this.description = stopping ? appHostStoppingDescription : resourceCountDescription(resources.length);
        this.tooltip = appHostDescription;
    }
}

class WorkspaceAppHostItem extends vscode.TreeItem {
    constructor(
        public readonly appHostPath: string,
        appHostName?: string,
        appHostDescription?: string,
        public readonly launching?: boolean,
        public readonly stopping = false
    ) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `workspace-apphost:${getComparisonKey(path.resolve(appHostPath))}`;

        if (stopping) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.description = appHostStoppingDescription;
            this.contextValue = 'workspaceAppHostStopping';
        } else if (launching) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.description = appHostStartingDescription;
            this.contextValue = 'workspaceAppHostLaunching';
        } else {
            this.iconPath = new vscode.ThemeIcon(
                appHostPath.endsWith('.csproj') ? 'server-process' : 'file-code',
                new vscode.ThemeColor('disabledForeground')
            );
            this.contextValue = 'workspaceAppHost';
        }

        this.tooltip = appHostDescription;
    }
}

class WorkspaceAppHostActionItem extends vscode.TreeItem {
    constructor(parent: WorkspaceAppHostItem, action: 'openSource' | 'run' | 'debug') {
        const label = action === 'openSource'
            ? appHostOpenSourceActionLabel
            : action === 'run'
                ? appHostRunActionLabel
                : appHostDebugActionLabel;
        super(label, vscode.TreeItemCollapsibleState.None);
        this.id = `${parent.id}:action:${action}`;
        this.iconPath = new vscode.ThemeIcon(action === 'debug' ? 'debug-alt' : action === 'run' ? 'play' : 'go-to-file');
        this.contextValue = `workspaceAppHostAction:${action}`;
        this.command = {
            command: action === 'openSource'
                ? 'aspire-vscode.openAppHostSource'
                : action === 'run'
                    ? 'aspire-vscode.runAppHost'
                    : 'aspire-vscode.debugAppHost',
            title: label,
            arguments: [parent]
        };
    }
}

class WorkspaceAppHostPathItem extends vscode.TreeItem {
    constructor(parent: WorkspaceAppHostItem) {
        super(appHostPathLabel, vscode.TreeItemCollapsibleState.None);
        this.id = `${parent.id}:path`;
        this.iconPath = new vscode.ThemeIcon('file-directory');
        this.contextValue = 'workspaceAppHostPath';
        this.description = parent.appHostPath;
        this.tooltip = parent.appHostPath;
    }
}

class WorkspaceAppHostsGroupItem extends vscode.TreeItem {
    constructor(public readonly appHosts: WorkspaceAppHostItem[]) {
        super(workspaceAppHostsGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-apphosts-group';
        this.iconPath = new vscode.ThemeIcon('folder');
        this.contextValue = 'workspaceAppHostsGroup';
        this.description = `(${appHosts.length})`;
    }
}

class RunningAppHostsGroupItem extends vscode.TreeItem {
    constructor(public readonly runningAppHosts: ReadonlyArray<AppHostItem | WorkspaceResourcesItem>) {
        super(runningAppHostsGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'running-apphosts-group';
        this.iconPath = new vscode.ThemeIcon('folder-active', new vscode.ThemeColor('aspire.brandPurple'));
        this.contextValue = 'runningAppHostsGroup';
        this.description = `(${runningAppHosts.length})`;
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

class CommandsGroupItem extends vscode.TreeItem {
    constructor(public readonly resource: ResourceJson, public readonly resourceItem: ResourceItem, parentId: string) {
        super(commandsLabel, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `${parentId}:commands`;
        this.iconPath = new vscode.ThemeIcon('terminal');
        this.contextValue = 'commandsGroup';
    }
}

class ResourceCommandItem extends vscode.TreeItem {
    constructor(
        public readonly commandName: string,
        public readonly commandJson: ResourceCommandJson,
        public readonly resourceItem: ResourceItem,
        parentId: string
    ) {
        const label = commandJson.displayName ?? commandName;
        super(label, vscode.TreeItemCollapsibleState.None);
        this.id = `${parentId}:command:${commandName}`;
        this.tooltip = commandJson.description ?? undefined;

        const isEnabled = isEnabledCommand(commandJson);

        this.iconPath = getResourceCommandIcon(commandName, isEnabled);
        if (isEnabled) {
            this.contextValue = 'resourceCommand:enabled';
        } else {
            this.description = resourceCommandDisabledDescription;
            this.contextValue = 'resourceCommand:disabled';
        }
    }
}

function getParentResourceName(resource: ResourceJson): string | null {
    return resource.properties?.['resource.parentName'] ?? null;
}

class ResourceItem extends vscode.TreeItem {
    constructor(
        public readonly resource: ResourceJson,
        public readonly appHostPid: number | null,
        hasChildren: boolean,
        public readonly allResources?: readonly ResourceJson[],
        public readonly appHostPath?: string
    ) {
        const label = resource.displayName ?? resource.name;
        const hasUrls = getVisibleResourceUrls(resource).length > 0;
        const hasHealthReports = resource.healthReports && Object.keys(resource.healthReports).length > 0;
        const hasCommands = resource.commands && getVisibleCommands(resource.commands).length > 0;
        const hasExpandableContent = hasChildren || hasUrls || hasHealthReports || hasCommands;
        const collapsible = hasChildren
            ? vscode.TreeItemCollapsibleState.Expanded
            : hasExpandableContent ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None;
        super(label, collapsible);
        const ownerId = appHostPid !== null
            ? appHostPid.toString()
            : appHostPath ? getComparisonKey(path.resolve(appHostPath)) : 'workspace';
        this.id = `resource:${ownerId}:${resource.name}`;
        this.iconPath = getResourceIcon(resource);
        this.description = buildResourceDescription(resource);
        this.tooltip = buildResourceTooltip(resource);
        this.contextValue = getResourceContextValue(resource);
    }
}

export function getResourceContextValue(resource: ResourceJson): string {
    const commands = resource.commands;
    const parts = ['resource'];
    if (hasEnabledCommand(commands, 'start') || hasEnabledCommand(commands, 'resource-start')) {
        parts.push('canStart');
    }
    if (hasEnabledCommand(commands, 'stop') || hasEnabledCommand(commands, 'resource-stop')) {
        parts.push('canStop');
    }
    if (hasEnabledCommand(commands, 'restart') || hasEnabledCommand(commands, 'resource-restart')) {
        parts.push('canRestart');
    }
    return parts.join(':');
}

function hasEnabledCommand(commands: Record<string, ResourceCommandJson> | null | undefined, commandName: string): boolean {
    const command = commands?.[commandName];
    return isCommandVisibleToUi(command) && isEnabledCommand(command);
}

export function getResourceIcon(resource: ResourceJson): vscode.ThemeIcon {
    const state = resource.state;
    const health = resource.healthStatus;
    switch (state) {
        case ResourceState.ValueMissing:
            return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
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
        parts.push(getResourceStateDescription(state));
    }
    const parameterValue = getParameterValueDescription(resource);
    if (parameterValue) {
        parts.push(parameterValue);
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
        md.appendMarkdown(`${tooltipState(getResourceStateDescription(resource.state))}\n\n`);
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
    const urls = getLinkableResourceUrls(resource);
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
    private static readonly _stoppingStateSafetyTimeoutMs = 120000;

    private readonly _onDidChangeTreeData = new vscode.EventEmitter<TreeElement | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private readonly _onDidChangeStoppingState = new vscode.EventEmitter<void>();
    readonly onDidChangeStoppingState = this._onDidChangeStoppingState.event;

    private readonly _onDidChangeContent = new vscode.EventEmitter<vscode.Uri>();
    readonly onDidChange = this._onDidChangeContent.event;

    private readonly _dataSubscription: vscode.Disposable;
    private readonly _launchingSubscription: vscode.Disposable;
    private readonly _stoppingAppHostTimeouts = new Map<string, ReturnType<typeof setTimeout>>();
    private _contentProviderRegistration: vscode.Disposable | undefined;
    private readonly _appHostSourceContents = new Map<string, string>();
    private _treeView: vscode.TreeView<TreeElement> | undefined;

    private _documentCloseSubscription: vscode.Disposable | undefined;

    constructor(
        private readonly _repository: AppHostDataRepository,
        private readonly _terminalProvider: AspireTerminalProvider,
        private readonly _launchService: AppHostLaunchService,
        private readonly _secretWarningState?: vscode.Memento,
    ) {
        this._dataSubscription = this._repository.onDidChangeData(() => {
            this._clearLaunchingPathsForRunningAppHosts();
            this._clearStoppingPathsForStoppedAppHosts();
            this._onDidChangeTreeData.fire();
        });

        // When the launch service's launching state changes, refresh the tree.
        this._launchingSubscription = this._launchService.onDidChangeLaunchingState(() => {
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

    get stoppingPaths(): readonly string[] {
        return Array.from(this._stoppingAppHostTimeouts.keys());
    }

    dispose(): void {
        this._dataSubscription.dispose();
        this._launchingSubscription.dispose();
        for (const timeout of this._stoppingAppHostTimeouts.values()) {
            clearTimeout(timeout);
        }
        this._stoppingAppHostTimeouts.clear();
        this._contentProviderRegistration?.dispose();
        this._documentCloseSubscription?.dispose();
        this._onDidChangeTreeData.dispose();
        this._onDidChangeStoppingState.dispose();
        this._onDidChangeContent.dispose();
    }

    setTreeView(treeView: vscode.TreeView<TreeElement>): void {
        this._treeView = treeView;
    }

    // When a launching AppHost appears in the running list, clear it from the launch service.
    private _clearLaunchingPathsForRunningAppHosts(): void {
        for (const appHost of this._repository.appHosts) {
            this._launchService.clearMatchingLaunching(appHost.appHostPath);
        }
    }

    private _trackStoppingAppHost(appHostPath: string): void {
        const existingKey = this._findStoppingAppHostKey(appHostPath);
        const key = existingKey ?? getComparisonKey(path.normalize(path.resolve(appHostPath)));
        const existingTimeout = this._stoppingAppHostTimeouts.get(key);
        if (existingTimeout) {
            clearTimeout(existingTimeout);
        }

        // The terminal command does not expose completion/failure, so clear the optimistic
        // UI state eventually even if no repository refresh arrives after a failed stop.
        const timeout = setTimeout(() => {
            this._clearStoppingAppHost(key, true);
        }, AspireAppHostTreeProvider._stoppingStateSafetyTimeoutMs);
        (timeout as { unref?: () => void }).unref?.();

        this._stoppingAppHostTimeouts.set(key, timeout);
        this._onDidChangeStoppingState.fire();
    }

    private _isStoppingAppHost(appHostPath: string | undefined): boolean {
        return this._findStoppingAppHostKey(appHostPath) !== undefined;
    }

    private _findStoppingAppHostKey(appHostPath: string | undefined): string | undefined {
        if (!appHostPath) {
            return undefined;
        }

        return Array.from(this._stoppingAppHostTimeouts.keys()).find(stoppingPath => isMatchingAppHostPath(stoppingPath, appHostPath));
    }

    private _clearStoppingAppHost(key: string, fireChangeEvent: boolean): void {
        const timeout = this._stoppingAppHostTimeouts.get(key);
        if (!timeout) {
            return;
        }

        clearTimeout(timeout);
        this._stoppingAppHostTimeouts.delete(key);
        this._onDidChangeStoppingState.fire();

        if (fireChangeEvent) {
            this._onDidChangeTreeData.fire();
        }
    }

    private _clearStoppingPathsForStoppedAppHosts(): void {
        const runningAppHostPaths = [
            ...this._repository.appHosts.map(appHost => appHost.appHostPath),
            this._repository.workspaceAppHost?.appHostPath,
        ].filter(path => path !== undefined);

        for (const stoppingPath of Array.from(this._stoppingAppHostTimeouts.keys())) {
            if (!runningAppHostPaths.some(runningPath => isMatchingAppHostPath(stoppingPath, runningPath))) {
                this._clearStoppingAppHost(stoppingPath, false);
            }
        }
    }

    findResourceElement(resourceName: string, appHostPath?: string): TreeElement | undefined {
        const allChildren = this.getChildren();
        if (appHostPath) {
            const appHostElement = this.findAppHostElement(appHostPath);
            return appHostElement ? this._findResourceInTree([appHostElement], resourceName) : undefined;
        }

        return this._findResourceInTree(allChildren, resourceName);
    }

    findEndpointElement(options?: { appHostPath?: string; resourceName?: string; url?: string }): TreeElement | undefined {
        const rootElements = options?.appHostPath
            ? this._getElementsForAppHostPath(options.appHostPath)
            : this.getChildren();

        return this._findEndpointInTree(rootElements, options?.resourceName, options?.url);
    }

    findResourceCommandElement(options: { appHostPath?: string; resourceName: string; commandName: string }): TreeElement | undefined {
        const rootElements = options.appHostPath
            ? this._getElementsForAppHostPath(options.appHostPath)
            : this.getChildren();

        const resource = this._findResourceInTree(rootElements, options.resourceName);
        if (!(resource instanceof ResourceItem)) {
            return undefined;
        }

        const commandsGroup = this.getChildren(resource).find(child => child instanceof CommandsGroupItem);
        return commandsGroup
            ? this.getChildren(commandsGroup).find(child => child instanceof ResourceCommandItem && child.commandName === options.commandName)
            : undefined;
    }

    findLogFileElement(appHostPath?: string): TreeElement | undefined {
        const rootElements = appHostPath
            ? this._getElementsForAppHostPath(appHostPath)
            : this.getChildren();

        return this._findLogFileInTree(rootElements);
    }

    private _getElementsForAppHostPath(appHostPath: string): TreeElement[] {
        const appHostElement = this.findAppHostElement(appHostPath);
        return appHostElement ? [appHostElement] : [];
    }

    /**
     * Finds the {@link AppHostItem} (global mode) or {@link WorkspaceResourcesItem}
     * (workspace mode) that corresponds to the given AppHost path.
     *
     * Matching prefers an exact path match, then falls back to an unambiguous
     * same-directory project/source match, which is needed because C# AppHost
     * paths can point at either the `.csproj` file or the sibling source file.
     */
    findAppHostElement(appHostPath: string): TreeElement | undefined {
        if (!appHostPath) {
            return undefined;
        }

        // Workspace mode wraps running/idle items in group elements, so flatten one level
        // of group children before matching. Group items themselves never match a path.
        const topLevel = this.getChildren();
        const elements: TreeElement[] = [];
        for (const element of topLevel) {
            if (element instanceof WorkspaceAppHostsGroupItem || element instanceof RunningAppHostsGroupItem) {
                elements.push(...this.getChildren(element));
            } else {
                elements.push(element);
            }
        }

        const candidateElements: { element: TreeElement; appHostPath: string }[] = [];
        for (const element of elements) {
            if (element instanceof AppHostItem) {
                const hostPath = element.appHost.appHostPath;
                if (!hostPath) {
                    continue;
                }
                candidateElements.push({ element, appHostPath: hostPath });
            } else if (element instanceof WorkspaceResourcesItem) {
                const hostPath = element.appHostPath;
                if (!hostPath) {
                    continue;
                }
                candidateElements.push({ element, appHostPath: hostPath });
            } else if (element instanceof WorkspaceAppHostItem) {
                candidateElements.push({ element, appHostPath: element.appHostPath });
            }
        }

        const exactMatch = candidateElements.find(candidate => isSamePath(candidate.appHostPath, appHostPath));
        if (exactMatch) {
            return exactMatch.element;
        }

        const fallbackMatches = candidateElements.filter(candidate => isProjectFileToSourceFileMatch(candidate.appHostPath, appHostPath));
        return fallbackMatches.length === 1 ? fallbackMatches[0].element : undefined;
    }

    private _findResourceInTree(elements: TreeElement[], resourceName: string): TreeElement | undefined {
        return this._findResourceInTreeCore(elements, resourceName, false)
            ?? this._findResourceInTreeCore(elements, resourceName, true);
    }

    private _findResourceInTreeCore(elements: TreeElement[], resourceName: string, includeDisplayName: boolean): TreeElement | undefined {
        for (const element of elements) {
            if (element instanceof ResourceItem) {
                if (resourceMatchesName(element.resource, resourceName, includeDisplayName)) {
                    return element;
                }
            }
            const children = this.getChildren(element);
            if (children.length > 0) {
                const found = this._findResourceInTreeCore(children, resourceName, includeDisplayName);
                if (found) {
                    return found;
                }
            }
        }
        return undefined;
    }

    private _findEndpointInTree(elements: TreeElement[], resourceName?: string, url?: string): TreeElement | undefined {
        if (resourceName) {
            const resource = this._findResourceInTree(elements, resourceName);
            if (resource instanceof ResourceItem) {
                return this.getChildren(resource).find(child => child instanceof EndpointUrlItem && (!url || child.url === url));
            }

            return undefined;
        }

        for (const element of elements) {
            if (element instanceof EndpointUrlItem && (!url || element.url === url)) {
                return element;
            }

            if (element instanceof ResourceItem) {
                const endpoint = this._findEndpointInTree(this.getChildren(element), undefined, url);
                if (endpoint) {
                    return endpoint;
                }
            } else {
                const endpoint = this._findEndpointInTree(this.getChildren(element), undefined, url);
                if (endpoint) {
                    return endpoint;
                }
            }
        }

        return undefined;
    }

    private _findLogFileInTree(elements: TreeElement[]): TreeElement | undefined {
        for (const element of elements) {
            if (element instanceof LogFileItem) {
                return element;
            }

            const logFile = this._findLogFileInTree(this.getChildren(element));
            if (logFile) {
                return logFile;
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
            const workspaceCandidatePaths = this._repository.workspaceAppHostCandidatePaths ?? [];
            const workspaceAppHostPaths = workspaceCandidatePaths.length > 0
                ? workspaceCandidatePaths
                : this._repository.appHosts.map(appHost => appHost.appHostPath);

            if (workspaceAppHostPaths.length > 1 || (workspaceResources.length === 0 && !workspaceAppHost)) {
                const selectedAppHostPath = workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath;
                const labels = shortenPaths(workspaceAppHostPaths);

                // When multiple workspace AppHosts are running, use global-style AppHostItem (nested view).
                // When only one is running, use flat WorkspaceResourcesItem.
                const runningItems: (AppHostItem | WorkspaceResourcesItem)[] = [];
                const workspaceItems: WorkspaceAppHostItem[] = [];

                for (let i = 0; i < workspaceAppHostPaths.length; i++) {
                    const candidatePath = workspaceAppHostPaths[i];
                    // Use directory-equivalent matching (not exact path) because `aspire ls`
                    // resolves to a `.csproj` while `aspire ps` can report the AppHost source file
                    // (e.g. Program.cs) in the same directory. AppHostDataRepository uses the same
                    // helper when filtering running AppHosts into _appHosts.
                    const runningAppHost = this._repository.appHosts.find(
                        appHost => isMatchingAppHostPath(appHost.appHostPath, candidatePath)
                    );
                    const launching = this._launchService.isLaunching(candidatePath);

                    if (!runningAppHost) {
                        workspaceItems.push(new WorkspaceAppHostItem(candidatePath, labels[i], vscode.workspace.asRelativePath(candidatePath), launching, this._isStoppingAppHost(candidatePath)));
                        continue;
                    }

                    // Merge workspace resources into the running AppHost if it's the selected one
                    // and its own resource list is empty (resources arrive via DCP separately).
                    const appHost = workspaceResources.length > 0
                        && selectedAppHostPath
                        && isMatchingAppHostPath(runningAppHost.appHostPath, selectedAppHostPath)
                        && hasNoResources(runningAppHost.resources)
                        ? { ...runningAppHost, resources: workspaceResources }
                        : runningAppHost;

                    if (runningItems.length > 0) {
                        // Multiple running — use global-style AppHostItem (nested view)
                        runningItems.push(new AppHostItem(appHost, labels[i], this._repository.workspaceAppHostDescription, this._isStoppingAppHost(appHost.appHostPath)));
                    } else {
                        const resources = [...appHost.resources ?? []];
                        const rawDashboardUrl = appHost.dashboardUrl ?? resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
                        const dashboardUrl = rawDashboardUrl ? stripResourceSuffix(rawDashboardUrl) : null;
                        runningItems.push(new WorkspaceResourcesItem(resources, dashboardUrl, appHost.appHostPath, appHost, labels[i], this._repository.workspaceAppHostDescription, this._isStoppingAppHost(appHost.appHostPath)));
                    }
                }

                // If multiple ended up running, convert the first to AppHostItem too
                if (runningItems.length > 1 && runningItems[0] instanceof WorkspaceResourcesItem) {
                    const first = runningItems[0];
                    const appHost = first.appHost!;
                    runningItems[0] = new AppHostItem(appHost, first.label as string, this._repository.workspaceAppHostDescription, this._isStoppingAppHost(appHost.appHostPath));
                }

                if (workspaceItems.length > 0 && runningItems.length > 0) {
                    // Wrap running items in a sibling group so both sets share the same
                    // indentation depth and the visual hierarchy reads symmetrically.
                    const runningGroup = new RunningAppHostsGroupItem(runningItems);
                    return [runningGroup, new WorkspaceAppHostsGroupItem(workspaceItems)];
                }
                // When nothing is running, still wrap idle items in the group so they
                // render under the "Workspace AppHosts" header. This keeps the tree shape
                // consistent with the mixed case and avoids loose root-level items.
                if (workspaceItems.length > 0) {
                    return [new WorkspaceAppHostsGroupItem(workspaceItems)];
                }
                return [...runningItems];
            }

            // Single candidate, running — show flat WorkspaceResourcesItem
            const resources = workspaceResources.length > 0
                ? workspaceResources
                : [...workspaceAppHost?.resources ?? []];
            const rawDashboardUrl = workspaceAppHost?.dashboardUrl ?? resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
            const dashboardUrl = rawDashboardUrl ? stripResourceSuffix(rawDashboardUrl) : null;
            const appHostPath = workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath;
            return [new WorkspaceResourcesItem(resources, dashboardUrl, appHostPath, workspaceAppHost, this._repository.workspaceAppHostName, this._repository.workspaceAppHostDescription, this._isStoppingAppHost(appHostPath))];
        }

        if (element instanceof AppHostItem || element instanceof ResourcesGroupItem) {
            return this._getGlobalChildren(element);
        }

        if (element instanceof WorkspaceAppHostsGroupItem) {
            return element.appHosts;
        }

        if (element instanceof RunningAppHostsGroupItem) {
            return [...element.runningAppHosts];
        }

        if (element instanceof WorkspaceAppHostItem) {
            const items: TreeElement[] = [new WorkspaceAppHostActionItem(element, 'openSource')];
            if (!element.launching && !element.stopping) {
                items.push(new WorkspaceAppHostActionItem(element, 'run'));
                items.push(new WorkspaceAppHostActionItem(element, 'debug'));
            }
            items.push(new WorkspaceAppHostPathItem(element));

            return items;
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
                items.push(new ResourceItem(resource, null, hasChildren, element.resources, element.appHostPath));
            }
            return items;
        }

        if (element instanceof ResourceItem) {
            const appHost = element.appHostPid !== null
                ? this._repository.appHosts.find(a => a.appHostPid === element.appHostPid)
                : undefined;
            const workspaceResources = [...this._repository.workspaceResources];
            const selectedAppHostPath = this._repository.workspaceAppHost?.appHostPath ?? this._repository.workspaceAppHostPath;
            const allResources = element.allResources ?? (appHost && workspaceResources.length > 0 && selectedAppHostPath && isMatchingAppHostPath(appHost.appHostPath, selectedAppHostPath) && hasNoResources(appHost.resources)
                ? workspaceResources
                : appHost?.resources ?? workspaceResources);
            return this._getResourceChildren(element, allResources);
        }

        if (element instanceof HealthChecksGroupItem) {
            return this._getHealthCheckChildren(element);
        }

        if (element instanceof CommandsGroupItem) {
            return this._getCommandChildren(element);
        }

        return [];
    }

    // ── Global mode tree ──

    private _getGlobalChildren(element?: TreeElement): TreeElement[] {
        if (!element) {
            const appHosts = this._repository.appHosts;
            const labels = shortenPaths(appHosts.map(appHost => appHost.appHostPath));
            return appHosts.map((appHost, index) => new AppHostItem(appHost, labels[index], this._repository.workspaceAppHostDescription, this._isStoppingAppHost(appHost.appHostPath)));
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
            const allResources = element.allResources ?? (this._repository.viewMode === 'workspace'
                ? [...this._repository.workspaceResources]
                : this._repository.appHosts.find(a => a.appHostPid === element.appHostPid)?.resources ?? []);
            return this._getResourceChildren(element, allResources);
        }
        if (element instanceof HealthChecksGroupItem) {
            return this._getHealthCheckChildren(element);
        }
        if (element instanceof CommandsGroupItem) {
            return this._getCommandChildren(element);
        }

        return [];
    }

    private _getResourceChildren(element: ResourceItem, allResources: readonly ResourceJson[]): TreeElement[] {
        const items: TreeElement[] = [];

        const children = allResources.filter(r => getParentResourceName(r) === element.resource.name);
        for (const child of sortResources(children)) {
            const hasChildren = allResources.some(r => getParentResourceName(r) === child.name);
            items.push(new ResourceItem(child, element.appHostPid, hasChildren, allResources, element.appHostPath));
        }

        const urls = getVisibleResourceUrls(element.resource);
        items.push(...urls.map(url => new EndpointUrlItem(url.url, url.displayName ?? url.url)));

        const reports = element.resource.healthReports;
        if (reports && Object.keys(reports).length > 0) {
            items.push(new HealthChecksGroupItem(element.resource, element.id!));
        }

        const commands = element.resource.commands;
        if (commands && getVisibleCommands(commands).length > 0) {
            items.push(new CommandsGroupItem(element.resource, element, element.id!));
        }

        return items;
    }

    private _getCommandChildren(element: CommandsGroupItem): TreeElement[] {
        const commands = element.resource.commands;
        if (!commands) {
            return [];
        }
        // Preserve the command order from the resource snapshot (registration order, e.g.
        // set-parameter before delete-parameter) so the tree matches the dashboard and the
        // command quick pick instead of an incidental alphabetical sort.
        return getVisibleCommands(commands)
            .map(([name, cmd]) => new ResourceCommandItem(name, cmd, element.resourceItem, element.id!));
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
        const url = await this._resolveDashboardUrl(element);
        if (url === undefined) {
            return;
        }

        if (url === null) {
            vscode.window.showInformationMessage(dashboardUrlNotFound);
            return;
        }

        if (!isWebDashboardUrl(url)) {
            vscode.window.showWarningMessage(dashboardUrlUnsupported);
            return;
        }

        await vscode.env.openExternal(vscode.Uri.parse(url));
    }

    async openDashboardToSide(element?: TreeElement): Promise<void> {
        const url = await this._resolveDashboardUrl(element);
        if (url === undefined) {
            return;
        }

        if (url === null) {
            vscode.window.showInformationMessage(dashboardUrlNotFound);
            return;
        }

        if (!isWebDashboardUrl(url)) {
            vscode.window.showWarningMessage(dashboardUrlUnsupported);
            return;
        }

        await openDashboardUrlToSide(url);
    }

    private async _resolveDashboardUrl(element?: TreeElement): Promise<string | null | undefined> {
        let url: string | null | undefined = null;

        if (element instanceof AppHostItem) {
            url = element.appHost.dashboardUrl;
        }

        if (element instanceof WorkspaceResourcesItem) {
            url = getBaseDashboardUrl(element.dashboardUrl);
        }

        if (!url && element === undefined) {
            if (this._repository.viewMode === 'workspace') {
                const resources = [...this._repository.workspaceResources];
                const resourceUrl = this._repository.workspaceAppHost?.dashboardUrl ?? resources.find(r => r.dashboardUrl)?.dashboardUrl ?? null;
                url = getBaseDashboardUrl(resourceUrl);
            }

            if (!url) {
                url = await this._resolveAppHostDashboardUrl();
            }
        }

        return url;
    }

    private async _resolveAppHostDashboardUrl(): Promise<string | null | undefined> {
        const appHosts = this._repository.appHosts.filter(a => a.dashboardUrl);
        if (appHosts.length === 1) {
            return appHosts[0].dashboardUrl!;
        }

        if (appHosts.length === 0) {
            return null;
        }

        const labels = shortenPaths(appHosts.map(a => a.appHostPath));
        const items = appHosts.map((a, index) => ({
            label: labels[index],
            description: pidDescription(a.appHostPid),
            dashboardUrl: a.dashboardUrl!,
        }));
        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: selectDashboardPlaceholder,
        });

        return selected?.dashboardUrl;
    }

    async runAppHost(element: WorkspaceAppHostItem | undefined, noDebug: boolean): Promise<void> {
        const appHostPath = element?.appHostPath;
        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }

        try {
            await this._launchService.launch(appHostPath, 'run', noDebug);
        } catch (err) {
            vscode.window.showErrorMessage(errorMessage(err));
            throw err;
        }
    }

    async stopAppHost(element: AppHostItem | WorkspaceResourcesItem | WorkspaceAppHostItem): Promise<void> {
        const appHostPath = element instanceof AppHostItem ? element.appHost.appHostPath : element.appHostPath;
        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }

        this._trackStoppingAppHost(appHostPath);
        this._onDidChangeTreeData.fire();
        try {
            await this._terminalProvider.sendAspireCommandToAspireTerminal(['stop', '--apphost', shellArg(appHostPath)]);
        } catch (err) {
            const stoppingKey = this._findStoppingAppHostKey(appHostPath);
            if (stoppingKey) {
                this._clearStoppingAppHost(stoppingKey, true);
            }
            throw err;
        }
    }

    async openAppHostSource(element?: AppHostItem | WorkspaceResourcesItem | WorkspaceAppHostItem): Promise<void> {
        if (!element || !(element instanceof AppHostItem || element instanceof WorkspaceResourcesItem || element instanceof WorkspaceAppHostItem)) {
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

    async stopResource(element: ResourceItem): Promise<void> {
        await this._runResourceCommand(element, 'stop');
    }

    async startResource(element: ResourceItem): Promise<void> {
        await this._runResourceCommand(element, 'start');
    }

    async restartResource(element: ResourceItem): Promise<void> {
        await this._runResourceCommand(element, 'restart');
    }

    async viewResourceLogs(element: ResourceItem): Promise<void> {
        // aspire logs accepts the resource display name, not the internal name
        const resourceName = element.resource.displayName ?? element.resource.name;
        if (this._repository.viewMode === 'workspace') {
            const appHostPath = this._getAppHostPathForResource(element);
            const command = appHostPath
                ? ['logs', shellArg(resourceName), '--apphost', shellArg(appHostPath)]
                : ['logs', shellArg(resourceName)];
            await this._terminalProvider.sendAspireCommandToAspireTerminal(command);
            return;
        }
        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        await this._terminalProvider.sendAspireCommandToAspireTerminal(['logs', shellArg(resourceName), '--apphost', shellArg(appHost.appHostPath)]);
    }

    async executeResourceCommand(element: ResourceItem): Promise<void> {
        const commands = element.resource.commands;
        if (!commands || Object.keys(commands).length === 0) {
            vscode.window.showInformationMessage(noCommandsAvailable);
            return;
        }

        const items = Object.entries(commands)
            .filter(([, cmd]) => isCommandVisibleToUi(cmd) && isEnabledCommand(cmd))
            .sort(compareResourceCommands)
            .map(([name, cmd]) => ({
                label: name,
                description: cmd.description ?? undefined,
                command: cmd,
            }));

        if (items.length === 0) {
            vscode.window.showInformationMessage(noCommandsAvailable);
            return;
        }

        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: selectCommandPlaceholder,
        });

        if (!selected) {
            throw new vscode.CancellationError();
        }

        const commandArguments = await collectResourceCommandArguments(selected.label, selected.command, {
            secretWarningState: this._secretWarningState,
            loadDynamicArguments: values => this._loadResourceCommandArguments(element, selected.label, values),
        });
        if (commandArguments === undefined) {
            throw new vscode.CancellationError();
        }

        await this._runResourceCommand(element, selected.label, commandArguments.args, commandArguments.containsSecret);
    }

    async executeResourceCommandItem(element: ResourceCommandItem): Promise<void> {
        const commandName = element.commandName;
        const command = element.commandJson;
        const resourceItem = element.resourceItem;

        if (!isEnabledCommand(command)) {
            vscode.window.showInformationMessage(noCommandsAvailable);
            return;
        }

        const commandArguments = await collectResourceCommandArguments(commandName, command, {
            secretWarningState: this._secretWarningState,
            loadDynamicArguments: values => this._loadResourceCommandArguments(resourceItem, commandName, values),
        });
        if (commandArguments === undefined) {
            return;
        }

        await this._runResourceCommand(resourceItem, commandName, commandArguments.args, commandArguments.containsSecret);
    }

    async copyAppHostPath(element: AppHostItem | WorkspaceResourcesItem | WorkspaceAppHostItem): Promise<void> {
        const appHostPath = element instanceof AppHostItem ? element.appHost.appHostPath : element.appHostPath;
        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }
        await vscode.env.clipboard.writeText(appHostPath);
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

    private async _runResourceCommand(element: ResourceItem, commandName: string, additionalArgs?: string[], redactAdditionalArgs = false): Promise<void> {
        if (this._repository.viewMode === 'workspace') {
            const appHostPath = this._getAppHostPathForResource(element);
            const command = appHostPath
                ? ['resource', shellArg(element.resource.name), shellArg(commandName), '--apphost', shellArg(appHostPath)]
                : ['resource', shellArg(element.resource.name), shellArg(commandName)];
            await this._terminalProvider.sendAspireCommandToAspireTerminal(command, true, additionalArgs, { redactAdditionalArgs });
            return;
        }

        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        await this._terminalProvider.sendAspireCommandToAspireTerminal(['resource', shellArg(element.resource.name), shellArg(commandName), '--apphost', shellArg(appHost.appHostPath)], true, additionalArgs, { redactAdditionalArgs });
    }

    private async _loadResourceCommandArguments(element: ResourceItem, commandName: string, values: readonly ResourceCommandArgumentValue[]): Promise<ResourceCommandArgumentInputJson[] | undefined> {
        const appHostPath = this._repository.viewMode === 'workspace'
            ? this._getAppHostPathForResource(element)
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
        return element.appHostPath ?? this._findAppHostForResource(element)?.appHostPath ?? this._repository.workspaceAppHostPath;
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

function isWebDashboardUrl(url: string): boolean {
    try {
        const parsed = new URL(url);
        return parsed.protocol === 'http:' || parsed.protocol === 'https:';
    } catch {
        return false;
    }
}

async function openDashboardUrlToSide(url: string): Promise<void> {
    const commands = await vscode.commands.getCommands(true);
    if (commands.includes(integratedBrowserOpenCommand)) {
        // VS Code 1.123+ exposes integrated-browser side placement through
        // workbench.action.browser.open({ url, openToSide: true }).
        // See https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/browserView/electron-browser/features/browserTabManagementFeatures.ts
        await vscode.commands.executeCommand(integratedBrowserOpenCommand, { url, openToSide: true });
        return;
    }

    await vscode.commands.executeCommand('simpleBrowser.api.open', vscode.Uri.parse(url), {
        viewColumn: vscode.ViewColumn.Beside,
        preserveFocus: false,
    });
}

function isProjectFileToSourceFileMatch(left: string, right: string): boolean {
    const normalizedLeft = path.normalize(left);
    const normalizedRight = path.normalize(right);
    return isSamePath(path.dirname(normalizedLeft), path.dirname(normalizedRight)) &&
        ((isProjectFile(normalizedLeft) && isAppHostSourceFile(normalizedRight)) ||
            (isAppHostSourceFile(normalizedLeft) && isProjectFile(normalizedRight)));
}

function isProjectFile(value: string): boolean {
    return path.extname(value).toLowerCase() === '.csproj';
}

function isAppHostSourceFile(value: string): boolean {
    const fileName = path.basename(value).toLowerCase();
    return fileName === 'apphost.cs' || fileName === 'program.cs';
}

function resourceMatchesName(resource: ResourceJson, resourceName: string, includeDisplayName: boolean): boolean {
    return resource.name === resourceName || (includeDisplayName && resource.displayName === resourceName);
}

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
