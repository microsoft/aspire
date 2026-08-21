import * as path from 'path';
import * as vscode from 'vscode';
import {
    resourcesGroupLabel,
    healthChecksLabel,
    healthCheckDescription,
    commandsLabel,
    resourceCommandDisabledDescription,
} from '../../loc/strings';
import { isLinkableUrl } from '../../utils/urlSchemes';
import { ResourceCommandJson, ResourceJson } from '../../data/AppHostDataRepository';
import { getComparisonKey } from '../../utils/paths/comparison';
import {
    buildResourceDescription,
    buildResourceTooltip,
    getResourceCommandIcon,
    getResourceContextValue,
    getResourceIcon,
    getVisibleCommands,
    getVisibleResourceUrls,
    isEnabledCommand,
} from '../treePresentation';

export class EndpointUrlItem extends vscode.TreeItem {
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

export class ResourcesGroupItem extends vscode.TreeItem {
    constructor(
        public readonly resources: ResourceJson[],
        public readonly appHostPid: number,
        public readonly appHostPath: string,
    ) {
        super(resourcesGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `resources:${getTreeItemOwnerId(appHostPath, appHostPid)}`;
        this.iconPath = new vscode.ThemeIcon('layers', new vscode.ThemeColor('aspire.brandPurple'));
        this.contextValue = 'resourcesGroup';
        this.description = `(${resources.length})`;
    }
}

export class HealthChecksGroupItem extends vscode.TreeItem {
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

export class HealthCheckItem extends vscode.TreeItem {
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

export class CommandsGroupItem extends vscode.TreeItem {
    constructor(public readonly resource: ResourceJson, public readonly resourceItem: ResourceItem, parentId: string) {
        super(commandsLabel, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `${parentId}:commands`;
        this.iconPath = new vscode.ThemeIcon('terminal');
        this.contextValue = 'commandsGroup';
    }
}

export class ResourceCommandItem extends vscode.TreeItem {
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

export class ResourceItem extends vscode.TreeItem {
    constructor(
        public readonly resource: ResourceJson,
        public readonly appHostPid: number | null,
        hasChildren: boolean,
        public readonly allResources?: readonly ResourceJson[],
        public readonly appHostPath?: string,
        canAttachDebugger = false,
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
        this.id = `resource:${getTreeItemOwnerId(appHostPath, appHostPid)}:${resource.name}`;
        this.iconPath = getResourceIcon(resource);
        this.description = buildResourceDescription(resource);
        this.tooltip = buildResourceTooltip(resource);
        this.contextValue = getResourceContextValue(resource, canAttachDebugger);
    }
}

function getTreeItemOwnerId(appHostPath: string | undefined, appHostPid: number | null): string {
    const pathId = appHostPath ? getComparisonKey(path.resolve(appHostPath)) : 'workspace';
    const processId = appHostPid ?? 'workspace';
    return `${pathId}:pid:${processId}`;
}
