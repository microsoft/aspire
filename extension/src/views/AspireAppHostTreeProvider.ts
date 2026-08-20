import * as path from 'path';
import * as vscode from 'vscode';
import { AspireTerminalProvider, ShellArg, shellArg } from '../utils/AspireTerminalProvider';
import { getCliPathTargetForUri, windowCliPathTarget } from '../utils/cliPathVariables';
import { compareResourceCommands } from '../utils/resourceDisplay';
import {
    pidDescription,
    dashboardLabel,
    noCommandsAvailable,
    selectCommandPlaceholder,
    selectDashboardPlaceholder,
    appHostPathCopiedToClipboard,
    appHostPathInvalid,
    appHostSourceNotFound,
    appHostSourceOpenFailed,
    logFileOpenFailed,
    logFilePathInvalid,
    dashboardUrlNotFound,
    dashboardUrlUnsupported,
    errorMessage,
} from '../loc/strings';
import { stripResourceSuffix } from '../utils/urlSchemes';
import {
    AppHostDataRepository,
    AppHostDisplayInfo,
    ResourceCommandArgumentInputJson,
    ResourceJson,
    ViewMode,
    isAppHostPathUnderFolder,
    isMatchingAppHostPath,
    shortenPaths,
} from '../data/AppHostDataRepository';
import { collectResourceCommandArguments, ResourceCommandArgumentValue } from './ResourceCommandArguments';
import { createResourceCommandArgumentLoader } from './ResourceCommandArgumentsLoader';
import { executeResourceCommand as executeResourceCommandWithUi, getErrorMessage, type ResourceCommandExecutionOutcome } from './resourceCommandExecution';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import { isSameFileSystemEntry } from '../utils/appHostDiscovery';
import { isAppHostSourceFile, isProjectFile } from '../utils/paths/comparison';
import { isCommandCancellation } from '../utils/telemetry';
import {
    getParentResourceName,
    getTerminalReplicaIndex,
    getVisibleCommands,
    getVisibleResourceUrls,
    hasNoResources,
    integratedBrowserOpenCommand,
    isCommandVisibleToUi,
    isEnabledCommand,
    resolveAppHostSourcePath,
    sortResources,
} from './treePresentation';
import {
    AppHostItem,
    CommandsGroupItem,
    EndpointUrlItem,
    HealthCheckItem,
    HealthChecksGroupItem,
    LogFileItem,
    ResourceCommandItem,
    ResourceItem,
    ResourcesGroupItem,
    RunningAppHostsGroupItem,
    WorkspaceAppHostActionItem,
    WorkspaceAppHostItem,
    WorkspaceAppHostPathItem,
    WorkspaceAppHostsGroupItem,
    WorkspaceResourcesItem,
} from './treeItems';

type TreeElement = AppHostItem | EndpointUrlItem | ResourcesGroupItem | ResourceItem | WorkspaceResourcesItem | WorkspaceAppHostItem | WorkspaceAppHostsGroupItem | RunningAppHostsGroupItem | WorkspaceAppHostActionItem | WorkspaceAppHostPathItem | HealthChecksGroupItem | HealthCheckItem | LogFileItem | CommandsGroupItem | ResourceCommandItem;

function isSamePath(left: string, right: string): boolean {
    return isSameFileSystemEntry(left, right);
}

/**
 * Minimal clipboard abstraction used by tree actions. Depending on the concrete
 * `vscode.env.clipboard` in unit tests is flaky: it is unavailable on headless CI and remote
 * containers and gets corrupted by concurrent test execution. Injecting this seam lets tests
 * observe the copied value deterministically without touching the real OS clipboard.
 */
export interface Clipboard {
    writeText(value: string): Thenable<void>;
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
        private readonly _clipboard: Clipboard = vscode.env.clipboard,
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
            this._launchService.clearLaunchingForRunningAppHost(appHost.appHostPath);
        }
    }

    private _trackStoppingAppHost(appHostPath: string): void {
        const resolvedAppHostPath = this._findKnownRunningAppHostPath(appHostPath) ?? appHostPath;
        const existingKey = this._findStoppingAppHostKey(resolvedAppHostPath);
        const key = existingKey ?? path.normalize(path.resolve(resolvedAppHostPath));
        const existingTimeout = this._stoppingAppHostTimeouts.get(key);
        if (existingTimeout) {
            clearTimeout(existingTimeout);
        }

        // Keep a safety bound in case no repository refresh arrives after a successful stop.
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

    private _isKnownRunningAppHost(appHostPath: string | undefined): boolean {
        if (!appHostPath) {
            return false;
        }

        return this._findKnownRunningAppHostPath(appHostPath) !== undefined;
    }

    private _findKnownRunningAppHostPath(appHostPath: string): string | undefined {
        const runningAppHostPaths = this._getKnownRunningAppHostPaths();
        const exactMatch = runningAppHostPaths.find(runningPath => isMatchingAppHostPath(runningPath, appHostPath));
        if (exactMatch) {
            return exactMatch;
        }

        const folderMatches = runningAppHostPaths.filter(runningPath => isAppHostPathUnderFolder(runningPath, appHostPath));
        return folderMatches.length === 1 ? folderMatches[0] : undefined;
    }

    private _getKnownRunningAppHostPaths(): string[] {
        const paths: string[] = [];
        for (const appHostPath of [
            this._repository.workspaceAppHost?.appHostPath,
            ...this._repository.appHosts.map(appHost => appHost.appHostPath),
        ]) {
            if (appHostPath && !paths.some(existingPath => isSamePath(existingPath, appHostPath))) {
                paths.push(appHostPath);
            }
        }

        return paths;
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
        if (!element && this._repository.isLoading) {
            return [];
        }

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
            const runningAppHostPaths = this._repository.appHosts.map(appHost => appHost.appHostPath);
            const workspaceAppHostPaths = workspaceCandidatePaths.length > 0
                ? [
                    ...workspaceCandidatePaths,
                    ...runningAppHostPaths.filter(runningPath => !workspaceCandidatePaths.some(candidatePath => isMatchingAppHostPath(runningPath, candidatePath))),
                ]
                : runningAppHostPaths;

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
                    // Each set (running / idle) only gets a "(N)" grouping header when it
                    // contains two or more AppHosts. A lone AppHost on either side is surfaced
                    // directly as a top-level sibling instead of being wrapped in a "(1)" node
                    // that adds nesting and a redundant click target without value.
                    // See https://github.com/microsoft/aspire/issues/18420.
                    // A single running AppHost is a flat WorkspaceResourcesItem (resources shown
                    // inline), matching the pure single-running case below.
                    const runningChild = runningItems.length === 1
                        ? runningItems[0]
                        : new RunningAppHostsGroupItem(runningItems);
                    const workspaceChild = workspaceItems.length === 1
                        ? workspaceItems[0]
                        : new WorkspaceAppHostsGroupItem(workspaceItems);
                    return [runningChild, workspaceChild];
                }
                // For a single idle AppHost (nothing running), skip the "Workspace AppHosts"
                // grouping node and surface the AppHost directly at the root, for the same
                // reason as the mixed case above (mirrors VS Code's SCM view for a single repo).
                // See https://github.com/microsoft/aspire/issues/18420.
                if (workspaceItems.length === 1) {
                    return [workspaceItems[0]];
                }
                // When two or more idle AppHosts exist, wrap them under the "Workspace AppHosts"
                // header so the tree shape stays consistent and avoids loose root-level items.
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
            if (!isCommandCancellation(err)) {
                vscode.window.showErrorMessage(errorMessage(err));
            }
            throw err;
        }
    }

    notifyAppHostStopping(appHostPath: string, markStopping = true): void {
        if (!appHostPath) {
            return;
        }

        if (markStopping) {
            this._markAppHostStopping(appHostPath);
        }
        this._repository.requestAppHostStopRefresh?.(appHostPath);
    }

    private _markAppHostStopping(appHostPath: string): void {
        if (this._isKnownRunningAppHost(appHostPath)) {
            this._trackStoppingAppHost(appHostPath);
        }
        this._onDidChangeTreeData.fire();
    }

    async stopAppHost(element: AppHostItem | WorkspaceResourcesItem | WorkspaceAppHostItem): Promise<void> {
        const appHostPath = element instanceof AppHostItem ? element.appHost.appHostPath : element.appHostPath;
        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostSourceNotFound);
            return;
        }

        this._markAppHostStopping(appHostPath);
        try {
            const result = await this._launchService.stopAppHost(appHostPath);
            if (result.outcome === 'stopped') {
                this._repository.requestAppHostStopRefresh?.(appHostPath);
            } else {
                const stoppingKey = this._findStoppingAppHostKey(appHostPath);
                if (stoppingKey) {
                    this._clearStoppingAppHost(stoppingKey, true);
                }
            }
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

    async stopResource(element: ResourceItem): Promise<ResourceCommandExecutionOutcome | void> {
        return await this._runResourceCommand(element, 'stop');
    }

    async startResource(element: ResourceItem): Promise<ResourceCommandExecutionOutcome | void> {
        return await this._runResourceCommand(element, 'start');
    }

    async restartResource(element: ResourceItem): Promise<ResourceCommandExecutionOutcome | void> {
        return await this._runResourceCommand(element, 'restart');
    }

    async viewResourceLogs(element: ResourceItem): Promise<void> {
        // aspire logs accepts the resource display name, not the internal name
        const resourceName = element.resource.displayName ?? element.resource.name;
        if (this._repository.viewMode === 'workspace') {
            const appHostPath = this._getAppHostPathForResource(element);
            const command = appHostPath
                ? ['logs', shellArg(resourceName), '--apphost', shellArg(appHostPath)]
                : ['logs', shellArg(resourceName)];
            const target = appHostPath
                ? getCliPathTargetForUri(vscode.Uri.file(appHostPath))
                : windowCliPathTarget;
            await this._terminalProvider.sendAspireCommandToAspireTerminal(command, true, undefined, { target });
            return;
        }
        const appHost = this._findAppHostForResource(element);
        if (!appHost) {
            return;
        }
        const target = getCliPathTargetForUri(vscode.Uri.file(appHost.appHostPath));
        await this._terminalProvider.sendAspireCommandToAspireTerminal(['logs', shellArg(resourceName), '--apphost', shellArg(appHost.appHostPath)], true, undefined, { target });
    }

    async openResourceTerminal(element: ResourceItem): Promise<void> {
        const command: Array<string | ShellArg> = ['terminal', 'attach', shellArg(element.resource.name)];
        const appHostPath = this._getAppHostPathForResource(element);
        if (appHostPath) {
            command.push('--apphost', shellArg(appHostPath));
        }

        const replicaIndex = getTerminalReplicaIndex(element.resource);
        if (replicaIndex) {
            command.push('--replica', shellArg(replicaIndex));
        }

        const target = appHostPath
            ? getCliPathTargetForUri(vscode.Uri.file(appHostPath))
            : windowCliPathTarget;
        await this._terminalProvider.sendAspireCommandToAspireTerminal(command, true, undefined, { terminalTarget: 'editor', target });
    }

    async executeResourceCommand(element: ResourceItem): Promise<ResourceCommandExecutionOutcome | void> {
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

        return await this._runResourceCommand(element, selected.label, commandArguments.args);
    }

    async executeResourceCommandItem(element: ResourceCommandItem): Promise<ResourceCommandExecutionOutcome | void> {
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

        return await this._runResourceCommand(resourceItem, commandName, commandArguments.args);
    }

    async copyAppHostPath(element: AppHostItem | WorkspaceResourcesItem | WorkspaceAppHostItem): Promise<void> {
        const appHostPath = element instanceof AppHostItem ? element.appHost.appHostPath : element.appHostPath;
        if (!appHostPath) {
            vscode.window.showWarningMessage(appHostPathInvalid);
            return;
        }
        await this._clipboard.writeText(appHostPath);
        vscode.window.showInformationMessage(appHostPathCopiedToClipboard);
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
        await this._clipboard.writeText(element.logFilePath);
    }

    async copyEndpointUrl(element: EndpointUrlItem): Promise<void> {
        await this._clipboard.writeText(element.url);
    }

    async copyResourceName(element: ResourceItem): Promise<void> {
        const name = element.resource.displayName ?? element.resource.name;
        await this._clipboard.writeText(name);
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

    async openInIntegratedBrowser(element: EndpointUrlItem): Promise<void> {
        await vscode.commands.executeCommand('simpleBrowser.show', element.url);
    }

    private async _runResourceCommand(element: ResourceItem, commandName: string, additionalArgs?: string[]): Promise<ResourceCommandExecutionOutcome | void> {
        // Execute resource commands over the hidden CLI backchannel instead of typing into the
        // visible Aspire terminal. The CLI runs the command non-interactively, and any returned
        // value is surfaced in a read-only editor via showResourceCommandOutput. additionalArgs are
        // forwarded verbatim (they already carry the `--` delimiter and prompted values); secret
        // values are not echoed to a terminal, and the spawn diagnostics log redacts tokens after
        // the `--` delimiter, so no separate redaction flag is needed here.
        const appHostPath = this._repository.viewMode === 'workspace'
            ? this._getAppHostPathForResource(element)
            : this._findAppHostForResource(element)?.appHostPath;

        if (this._repository.viewMode !== 'workspace' && appHostPath === undefined) {
            return;
        }

        return await executeResourceCommandWithUi(
            this._repository,
            (resourceName, command, content, outputAppHostPath) => this.showResourceCommandOutput(resourceName, command, content, outputAppHostPath),
            {
                resourceName: element.resource.name,
                displayName: element.resource.displayName ?? element.resource.name,
                commandName,
                appHostPath: appHostPath ?? undefined,
                additionalArgs,
            });
    }

    async showResourceCommandOutput(resourceName: string, commandName: string, content: string, appHostPath?: string): Promise<void> {
        // Reuse the read-only aspire-source virtual document provider so returned command values open
        // in a normal editor the user can read, search, and copy from, without a save prompt.
        const safeName = `${resourceName}-${commandName}`.replace(/[^A-Za-z0-9._-]+/g, '_');
        const uri = vscode.Uri.from({
            scheme: 'aspire-source',
            path: `${safeName}-output.txt`,
            query: appHostPath === undefined ? undefined : `appHostPath=${encodeURIComponent(path.normalize(appHostPath))}`,
        });
        this._ensureContentProviderRegistered();
        this._appHostSourceContents.set(uri.toString(), content);
        this._onDidChangeContent.fire(uri);
        const document = await vscode.workspace.openTextDocument(uri);
        await vscode.window.showTextDocument(document, { preview: true });
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

function resourceMatchesName(resource: ResourceJson, resourceName: string, includeDisplayName: boolean): boolean {
    return resource.name === resourceName || (includeDisplayName && resource.displayName === resourceName);
}
