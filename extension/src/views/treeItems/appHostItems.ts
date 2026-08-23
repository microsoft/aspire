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
    appHostDeployActionLabel,
    appHostPublishActionLabel,
    appHostRunPipelineStepActionLabel,
    appHostDebugPipelineStepActionLabel,
    appHostPathLabel,
    resourceCountDescription,
    logFileLabel,
    appHostStartingDescription,
    appHostStoppingDescription,
    appHostDeployingDescription,
    appHostPublishingDescription,
    appHostRunningPipelineStepDescription,
    appHostDebuggingPipelineStepDescription,
    loadingPipelineSteps,
} from '../../loc/strings';
import { AppHostDisplayInfo, ResourceJson } from '../../data/AppHostDataRepository';
import { appHostIcon } from '../treePresentation';

/**
 * A durable non-Run operation (deploy/publish/do) that owns an AppHost until it finishes.
 * Unlike a Run - which is represented by the running AppHost itself - nothing else in the tree
 * shows that one of these is in flight, so the owning row renders it.
 */
export interface AppHostItemOperation {
    readonly command: 'deploy' | 'publish' | 'do';
    /** For `do`, whether the pipeline step runs without the debugger attached. */
    readonly noDebug: boolean;
}

/** Which non-Run actions the exact selected Aspire CLI supports for one AppHost. */
export interface AppHostActionAvailability {
    readonly deploy: boolean;
    readonly publish: boolean;
    readonly do: boolean;
}

function appHostOperationDescription(operation: AppHostItemOperation): string {
    switch (operation.command) {
        case 'deploy':
            return appHostDeployingDescription;
        case 'publish':
            return appHostPublishingDescription;
        case 'do':
            return operation.noDebug ? appHostRunningPipelineStepDescription : appHostDebuggingPipelineStepDescription;
    }
}

/**
 * Capability tokens are appended in a fixed `:canDeploy:canPublish:canDo` order so a package.json
 * `when` clause can match one action without enumerating every combination. Keep the order in sync
 * with the `view/item/context` regexes in package.json.
 *
 * Absent capabilities (the CLI has not been probed yet, or could not answer) intentionally render
 * no tokens, so an action stays hidden until the selected CLI is known to support it.
 */
function appHostActionContextSuffix(actions: AppHostActionAvailability | undefined): string {
    if (!actions) {
        return '';
    }

    return `${actions.deploy ? ':canDeploy' : ''}${actions.publish ? ':canPublish' : ''}${actions.do ? ':canDo' : ''}`;
}

export class AppHostItem extends vscode.TreeItem {
    constructor(
        public readonly appHost: AppHostDisplayInfo,
        label: string,
        appHostDescription?: string,
        stopping = false,
        public readonly operation?: AppHostItemOperation,
        public readonly actions?: AppHostActionAvailability
    ) {
        super(label, vscode.TreeItemCollapsibleState.Expanded);
        this.id = `apphost:${appHost.appHostPid}`;

        // A stop is an imminent transition of this row itself, so it outranks a durable
        // deploy/publish/do that only changes what the AppHost is busy with.
        if (stopping) {
            this.description = appHostStoppingDescription;
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.contextValue = 'appHost:stopping';
        } else if (operation) {
            this.description = appHostOperationDescription(operation);
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.contextValue = 'appHost:operating';
        } else {
            this.description = pidDescription(appHost.appHostPid);
            this.iconPath = appHostIcon(appHost.appHostPath);
            this.contextValue = `appHost${appHostActionContextSuffix(actions)}`;
        }

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
        stopping = false,
        public readonly operation?: AppHostItemOperation,
        public readonly actions?: AppHostActionAvailability
    ) {
        super(appHostName ?? workspaceAppHostLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.id = 'workspace-resources';

        // `hasAppHost` stays in the operating context value so the source, path and stop
        // affordances keyed off it survive a deploy/publish/do. Capability tokens are only added
        // alongside it, because every capability-gated action already requires a running AppHost
        // here (see the `view/item/context` entries in package.json).
        const baseContextValue = appHost ? 'workspaceResources:hasAppHost' : 'workspaceResources';
        if (stopping) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.contextValue = 'workspaceResources:stopping';
            this.description = appHostStoppingDescription;
        } else if (operation) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.contextValue = `${baseContextValue}:operating`;
            this.description = appHostOperationDescription(operation);
        } else {
            this.iconPath = appHostIcon(appHostPath);
            this.contextValue = `${baseContextValue}${appHost ? appHostActionContextSuffix(actions) : ''}`;
            this.description = resourceCountDescription(resources.length);
        }

        this.tooltip = appHostDescription;
    }
}

export class WorkspaceAppHostItem extends vscode.TreeItem {
    constructor(
        public readonly appHostPath: string,
        appHostName?: string,
        appHostDescription?: string,
        public readonly launching?: boolean,
        public readonly stopping = false,
        public readonly operation?: AppHostItemOperation,
        public readonly actions?: AppHostActionAvailability,
        collapsibleState = vscode.TreeItemCollapsibleState.Collapsed
    ) {
        super(appHostName ?? workspaceAppHostLabel, collapsibleState);
        this.id = `workspace-apphost:${path.resolve(appHostPath)}`;

        if (stopping) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.description = appHostStoppingDescription;
            this.contextValue = 'workspaceAppHostStopping';
        } else if (operation) {
            // A deploy/publish/do runs for minutes and is the only place it is visible, while a
            // Run's `Starting...` window is brief and ends with the AppHost moving to a running
            // row. The durable operation therefore outranks launching.
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.description = appHostOperationDescription(operation);
            this.contextValue = 'workspaceAppHostOperating';
        } else if (launching) {
            this.iconPath = new vscode.ThemeIcon('loading~spin');
            this.description = appHostStartingDescription;
            this.contextValue = 'workspaceAppHostLaunching';
        } else {
            this.iconPath = new vscode.ThemeIcon(
                appHostPath.endsWith('.csproj') ? 'server-process' : 'file-code',
                new vscode.ThemeColor('disabledForeground')
            );
            this.contextValue = `workspaceAppHost${appHostActionContextSuffix(actions)}`;
        }

        this.tooltip = appHostDescription;
    }
}

export type WorkspaceAppHostAction = 'openSource' | 'run' | 'debug' | 'deploy' | 'publish' | 'runPipelineStep' | 'debugPipelineStep';

const actionLabels: Record<WorkspaceAppHostAction, string> = {
    openSource: appHostOpenSourceActionLabel,
    run: appHostRunActionLabel,
    debug: appHostDebugActionLabel,
    deploy: appHostDeployActionLabel,
    publish: appHostPublishActionLabel,
    runPipelineStep: appHostRunPipelineStepActionLabel,
    debugPipelineStep: appHostDebugPipelineStepActionLabel,
};

const actionIcons: Record<WorkspaceAppHostAction, string> = {
    openSource: 'go-to-file',
    run: 'play',
    debug: 'debug-alt',
    deploy: 'cloud-upload',
    publish: 'package',
    runPipelineStep: 'run-all',
    debugPipelineStep: 'debug-all',
};

const actionCommands: Record<WorkspaceAppHostAction, string> = {
    openSource: 'aspire-vscode.openAppHostSource',
    run: 'aspire-vscode.runAppHost',
    debug: 'aspire-vscode.debugAppHost',
    deploy: 'aspire-vscode.deployAppHost',
    publish: 'aspire-vscode.publishAppHost',
    runPipelineStep: 'aspire-vscode.runPipelineStepAppHost',
    debugPipelineStep: 'aspire-vscode.debugPipelineStepAppHost',
};

export class WorkspaceAppHostActionItem extends vscode.TreeItem {
    constructor(parent: WorkspaceAppHostItem, action: WorkspaceAppHostAction, loading = false) {
        const label = actionLabels[action];
        super(label, vscode.TreeItemCollapsibleState.None);
        this.id = `${parent.id}:action:${action}`;
        this.iconPath = new vscode.ThemeIcon(loading ? 'loading~spin' : actionIcons[action]);
        this.contextValue = `workspaceAppHostAction:${action}${loading ? ':loading' : ''}`;
        if (loading) {
            this.description = loadingPipelineSteps;
        } else {
            this.command = {
                command: actionCommands[action],
                title: label,
                arguments: [parent]
            };
        }
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
