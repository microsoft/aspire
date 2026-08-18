import * as path from 'path';
import * as vscode from 'vscode';
import {
    pidDescription,
    workspaceAppHostLabel,
    workspaceAppHostsGroupLabel,
    runningAppHostsGroupLabel,
    appHostOpenSourceActionLabel,
    appHostRunActionLabel,
    appHostDebugActionLabel,
    appHostPathLabel,
    resourceCountDescription,
    logFileLabel,
    appHostStartingDescription,
    appHostStoppingDescription,
} from '../../loc/strings';
import { AppHostDisplayInfo, ResourceJson } from '../../data/AppHostDataRepository';
import { appHostIcon } from '../treePresentation';

export class AppHostItem extends vscode.TreeItem {
    constructor(public readonly appHost: AppHostDisplayInfo, label: string, appHostDescription?: string, stopping = false) {
        super(label, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `apphost:${appHost.appHostPid}`;
        this.description = stopping ? appHostStoppingDescription : pidDescription(appHost.appHostPid);
        this.iconPath = stopping ? new vscode.ThemeIcon('loading~spin') : appHostIcon(appHost.appHostPath);
        this.contextValue = stopping ? 'appHost:stopping' : 'appHost';
        this.tooltip = appHostDescription ? `${appHostDescription}\n${appHost.appHostPath}` : appHost.appHostPath;
    }
}

export class WorkspaceResourcesItem extends vscode.TreeItem {
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

export class WorkspaceAppHostItem extends vscode.TreeItem {
    constructor(
        public readonly appHostPath: string,
        appHostName?: string,
        appHostDescription?: string,
        public readonly launching?: boolean,
        public readonly stopping = false
    ) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `workspace-apphost:${path.resolve(appHostPath)}`;

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

export class WorkspaceAppHostActionItem extends vscode.TreeItem {
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

export class WorkspaceAppHostPathItem extends vscode.TreeItem {
    constructor(parent: WorkspaceAppHostItem) {
        super(appHostPathLabel, vscode.TreeItemCollapsibleState.None);
        this.id = `${parent.id}:path`;
        this.iconPath = new vscode.ThemeIcon('file-directory');
        this.contextValue = 'workspaceAppHostPath';
        this.description = parent.appHostPath;
        this.tooltip = parent.appHostPath;
        // Clicking the Path row copies the AppHost path, since that's the most obvious thing a user
        // expects when clicking a path. This mirrors WorkspaceAppHostActionItem/EndpointUrlItem and
        // reuses the same handler as the right-click context menu. See
        // https://github.com/microsoft/aspire/issues/18578.
        this.command = {
            command: 'aspire-vscode.copyAppHostPath',
            title: appHostPathLabel,
            arguments: [parent]
        };
    }
}

export class WorkspaceAppHostsGroupItem extends vscode.TreeItem {
    constructor(public readonly appHosts: WorkspaceAppHostItem[]) {
        super(workspaceAppHostsGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-apphosts-group';
        this.iconPath = new vscode.ThemeIcon('folder');
        this.contextValue = 'workspaceAppHostsGroup';
        this.description = `(${appHosts.length})`;
    }
}

export class RunningAppHostsGroupItem extends vscode.TreeItem {
    constructor(public readonly runningAppHosts: ReadonlyArray<AppHostItem | WorkspaceResourcesItem>) {
        super(runningAppHostsGroupLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'running-apphosts-group';
        this.iconPath = new vscode.ThemeIcon('folder-active', new vscode.ThemeColor('aspire.brandPurple'));
        this.contextValue = 'runningAppHostsGroup';
        this.description = `(${runningAppHosts.length})`;
    }
}

export class LogFileItem extends vscode.TreeItem {
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
