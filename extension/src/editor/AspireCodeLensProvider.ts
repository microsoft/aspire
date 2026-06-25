import * as vscode from 'vscode';
import { AppHostResourceParser, getParserForDocument } from './parsers/AppHostResourceParser';
// Import parsers to trigger self-registration
import './parsers/csharpAppHostParser';
import './parsers/jsTsAppHostParser';
import { AspireAppHostTreeProvider, isCommandVisibleToUi, isEnabledCommand } from '../views/AspireAppHostTreeProvider';
import { compareResourceCommands, getParameterValueDescription, getResourceStateDescription } from '../utils/resourceDisplay';
import { AppHostDataRepository, ResourceJson, AppHostDisplayInfo, ResourceCommandJson } from '../views/AppHostDataRepository';
import { findResourceState, findWorkspaceResourceState, matchesAppHostPathOrDirectory } from './resourceStateUtils';
import { ResourceState, HealthStatus, StateStyle, ResourceType } from './resourceConstants';
import {
    codeLensDebugPipelineStep,
    codeLensResourceRunning,
    codeLensResourceRunningWarning,
    codeLensResourceRunningError,
    codeLensResourceStarting,
    codeLensResourceStopping,
    codeLensResourceNotStarted,
    codeLensResourceWaiting,
    codeLensResourceStopped,
    codeLensResourceStoppedWithExitCode,
    codeLensResourceStoppedError,
    codeLensResourceStoppedErrorWithExitCode,
    codeLensResourceError,
    codeLensRestart,
    codeLensStop,
    codeLensStart,
    codeLensViewLogs,
    codeLensCommand,
    codeLensOpenDashboard,
    codeLensViewAppHostLogs,
    codeLensResourceValueMissing,
} from '../loc/strings';

export class AspireCodeLensProvider implements vscode.CodeLensProvider {
    private readonly _onDidChangeCodeLenses = new vscode.EventEmitter<void>();
    readonly onDidChangeCodeLenses = this._onDidChangeCodeLenses.event;

    private _disposables: vscode.Disposable[] = [];

    constructor(
        private readonly _treeProvider: AspireAppHostTreeProvider,
        private readonly _dataRepository: AppHostDataRepository,
    ) {
        // Re-compute lenses whenever the polling data changes
        this._disposables.push(
            _treeProvider.onDidChangeTreeData(() => this._onDidChangeCodeLenses.fire())
        );
    }

    provideCodeLenses(document: vscode.TextDocument, token: vscode.CancellationToken): vscode.ProviderResult<vscode.CodeLens[]> {
        return this._provideCodeLensesAsync(document, token);
    }

    private async _provideCodeLensesAsync(document: vscode.TextDocument, token: vscode.CancellationToken): Promise<vscode.CodeLens[] | undefined> {
        if (!vscode.workspace.getConfiguration('aspire').get<boolean>('enableCodeLens', true)) {
            return [];
        }

        const parser = await getParserForDocument(document);
        if (token.isCancellationRequested) {
            return undefined;
        }

        if (!parser) {
            return [];
        }

        const resources = await parser.parseResources(document);
        if (token.isCancellationRequested) {
            return undefined;
        }

        const appHosts = this._treeProvider.appHosts;
        const workspaceResources = this._treeProvider.workspaceResources;
        const workspaceAppHost = this._treeProvider.workspaceAppHost;
        const workspaceAppHostPath = this._treeProvider.workspaceAppHostPath ?? '';
        const globalAppHost = this._resolveGlobalAppHostForDocument(document, appHosts);
        const workspaceAppHostMatchesDocument = workspaceAppHostPath !== '' && this._documentMatchesAppHostPath(document, workspaceAppHostPath);
        const hasRunningData = globalAppHost !== undefined || (workspaceAppHostMatchesDocument && (workspaceResources.length > 0 || workspaceAppHost !== undefined));
        const findWorkspace = workspaceAppHostMatchesDocument
            ? findWorkspaceResourceState(workspaceResources, workspaceAppHostPath)
            : () => undefined;

        const lenses: vscode.CodeLens[] = [];

        // Builder-statement lenses (Open Dashboard + View Logs) appear only when this
        // document maps to a concretely-running AppHost — independent of whether any
        // Add* resource calls were found in the file.
        await this._addBuilderStatementLenses(lenses, document, parser, workspaceAppHostPath, workspaceResources);

        if (resources.length === 0) {
            return lenses;
        }

        for (const resource of resources) {
            // For pipeline steps the whole statement maps to a single Add*(...) call, so
            // anchoring at the top of the chain reads naturally.
            //
            // For resources, a single fluent chain can declare several (e.g.
            // `builder.AddPostgres("pg").AddDatabase("db")`). If we collapsed all of those
            // to the chain's start line their state/action lenses would stack on the same
            // line and the user couldn't tell which "Stopped" / which "Stop" belongs to
            // which resource. So when more than one resource shares a statement we anchor
            // each at its own call line; when a chain declares just one resource we use
            // the statement-start line so the lens sits above the whole declaration
            // (e.g. above `const nodePlayer = await builder` rather than between that
            // line and the `.addNodeApp(...)` call).
            const statementStart = resource.statementStartLine ?? resource.range.start.line;
            const sharedWithOthers = resource.kind === 'resource'
                && resources.some(other =>
                    other !== resource
                    && other.kind === 'resource'
                    && (other.statementStartLine ?? other.range.start.line) === statementStart);
            const lensLine = (resource.kind === 'pipelineStep' || !sharedWithOthers)
                ? statementStart
                : resource.range.start.line;
            const lineRange = new vscode.Range(lensLine, 0, lensLine, 0);

            if (resource.kind === 'pipelineStep') {
                // Pipeline steps get Debug lens when no AppHost is running
                if (!hasRunningData) {
                    this._addPipelineStepLenses(lenses, lineRange, resource.name);
                }
            } else if (resource.kind === 'resource') {
                // Resources get state lenses when live data is available
                if (hasRunningData) {
                    const match = (globalAppHost ? findResourceState([globalAppHost], resource.name) : undefined)
                        ?? findWorkspace(resource.name);
                    if (match) {
                        this._addStateLenses(lenses, lineRange, match.resource, match.appHost);
                    }
                }
            }
        }

        return lenses;
    }

    private _addPipelineStepLenses(lenses: vscode.CodeLens[], range: vscode.Range, stepName: string): void {
        lenses.push(new vscode.CodeLens(range, {
            title: codeLensDebugPipelineStep,
            command: 'aspire-vscode.codeLensDebugPipelineStep',
            tooltip: codeLensDebugPipelineStep,
            arguments: [stepName],
        }));
    }

    private async _addBuilderStatementLenses(
        lenses: vscode.CodeLens[],
        document: vscode.TextDocument,
        parser: AppHostResourceParser,
        workspaceAppHostPath: string,
        workspaceResources: readonly ResourceJson[],
    ): Promise<void> {
        const builderLine = await parser.findBuilderStatementLine?.(document);
        if (builderLine === undefined) {
            return;
        }

        // Only emit the lens when the document maps to a concretely-running AppHost.
        // This prevents stale lenses on AppHost files whose host is not currently running,
        // and avoids dispatching commands with a `.cs` source path the CLI cannot resolve.
        const appHostPath = this._resolveAppHostPathForDocument(document, workspaceAppHostPath, workspaceResources);
        if (appHostPath === undefined) {
            return;
        }

        const range = new vscode.Range(builderLine, 0, builderLine, 0);

        lenses.push(new vscode.CodeLens(range, {
            title: codeLensOpenDashboard,
            command: 'aspire-vscode.codeLensOpenDashboard',
            tooltip: codeLensOpenDashboard,
            arguments: [appHostPath],
        }));

        lenses.push(new vscode.CodeLens(range, {
            title: codeLensViewAppHostLogs,
            command: 'aspire-vscode.codeLensViewAppHostLogs',
            tooltip: codeLensViewAppHostLogs,
            arguments: [appHostPath],
        }));
    }

    /**
     * Resolves the running-AppHost path that the given document represents, or
     * `undefined` when the document cannot be tied to a running host.
     *
     * Resolution order:
     *  1. Exact path or same-directory match against {@link AppHostDataRepository.appHosts}
     *     (covers global mode and any workspace AppHosts that surface there).
     *  2. The repository's `workspaceAppHostPath` when workspace live data identifies
     *     a running AppHost and the document lives in the same directory as that AppHost.
     *
     * The document path itself is intentionally not used as a fallback — for C#
     * AppHosts the CLI requires a `.csproj`, not a `.cs` file.
     */
    private _resolveAppHostPathForDocument(
        document: vscode.TextDocument,
        workspaceAppHostPath: string,
        workspaceResources: readonly ResourceJson[],
    ): string | undefined {
        const docPath = document.uri.fsPath;
        const match = this._dataRepository.appHosts.find(host => {
            const hostPath = host.appHostPath;
            return matchesAppHostPathOrDirectory(docPath, hostPath);
        });
        if (match) {
            return match.appHostPath;
        }
        if (workspaceAppHostPath && (workspaceResources.length > 0 || this._dataRepository.workspaceAppHost !== undefined)) {
            if (matchesAppHostPathOrDirectory(docPath, workspaceAppHostPath)) {
                return workspaceAppHostPath;
            }
        }
        return undefined;
    }

    private _resolveGlobalAppHostForDocument(document: vscode.TextDocument, appHosts: readonly AppHostDisplayInfo[]): AppHostDisplayInfo | undefined {
        return appHosts.find(host => this._documentMatchesAppHostPath(document, host.appHostPath));
    }

    private _documentMatchesAppHostPath(document: vscode.TextDocument, appHostPath: string | undefined): boolean {
        if (!appHostPath) {
            return false;
        }

        return matchesAppHostPathOrDirectory(document.uri.fsPath, appHostPath);
    }

    private _addStateLenses(
        lenses: vscode.CodeLens[],
        range: vscode.Range,
        resource: ResourceJson,
        appHost: AppHostDisplayInfo,
    ): void {
        const state = resource.state ?? '';
        const stateStyle = resource.stateStyle ?? '';
        const healthStatus = resource.healthStatus;
        const commands = resource.commands ?? {};

        // State indicator lens (clickable — reveals resource in tree view)
        let stateLabel = getCodeLensStateLabel(state, stateStyle, resource.exitCode);
        if (healthStatus && healthStatus !== HealthStatus.Healthy) {
            const reports = resource.healthReports;
            if (reports) {
                const entries = Object.values(reports);
                const healthy = entries.filter(r => r.status === HealthStatus.Healthy).length;
                stateLabel += ` - (${healthStatus} ${healthy}/${entries.length})`;
            } else {
                stateLabel += ` - (${healthStatus})`;
            }
        }

        let tooltipText = `${resource.displayName ?? resource.name}: ${getResourceStateDescription(state)}${healthStatus ? ` (${healthStatus})` : ''}`;
        const reports = resource.healthReports;
        if (reports && healthStatus && healthStatus !== HealthStatus.Healthy) {
            const failing = Object.entries(reports).filter(([, r]) => r.status !== HealthStatus.Healthy);
            if (failing.length > 0) {
                tooltipText += '\n' + failing.map(([name, r]) => `  ${name}: ${r.status}${r.description ? ` - ${r.description}` : ''}`).join('\n');
            }
        }

        lenses.push(new vscode.CodeLens(range, {
            title: stateLabel,
            command: 'aspire-vscode.codeLensRevealResource',
            tooltip: tooltipText,
            arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
        }));

        // Parameter value lens (secrets masked, long values truncated) so the value is
        // visible inline next to the state, matching the dashboard and tree view.
        const parameterValue = getParameterValueDescription(resource);
        if (parameterValue !== undefined) {
            lenses.push(new vscode.CodeLens(range, {
                title: parameterValue,
                command: 'aspire-vscode.codeLensRevealResource',
                tooltip: parameterValue,
                arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
            }));
        }

        // Action lenses based on available commands
        const restartCommand = getEnabledCommand(commands, 'restart', 'resource-restart');
        if (restartCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensRestart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensRestart,
                arguments: [resource.name, 'restart', appHost.appHostPath, restartCommand],
            }));
        }

        const stopCommand = getEnabledCommand(commands, 'stop', 'resource-stop');
        if (stopCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStop,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStop,
                arguments: [resource.name, 'stop', appHost.appHostPath, stopCommand],
            }));
        }

        const startCommand = getEnabledCommand(commands, 'start', 'resource-start');
        if (startCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStart,
                arguments: [resource.name, 'start', appHost.appHostPath, startCommand],
            }));
        }

        // View Logs lens (not applicable to parameters)
        if (resource.resourceType !== ResourceType.Parameter) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensViewLogs,
                command: 'aspire-vscode.codeLensViewLogs',
                tooltip: codeLensViewLogs,
                arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
            }));
        }

        // Custom commands (non-standard ones like "Reset Database")
        const standardCommands = new Set(['restart', 'resource-restart', 'stop', 'resource-stop', 'start', 'resource-start']);
        // Sort by (order, name) so custom command lenses appear in the dashboard registration order.
        const customCommands = (Object.entries(commands) as [string, ResourceCommandJson][])
            .sort(compareResourceCommands);
        for (const [cmdName, cmd] of customCommands) {
            if (!standardCommands.has(cmdName) && isEnabledCommand(cmd) && isCommandVisibleToUi(cmd)) {
                const displayName = getNormalizedCommandText(cmd.displayName);
                const description = getNormalizedCommandText(cmd.description);
                const label = codeLensCommand(displayName ?? cmdName);
                lenses.push(new vscode.CodeLens(range, {
                    title: label,
                    command: 'aspire-vscode.codeLensResourceAction',
                    tooltip: description ?? displayName ?? cmdName,
                    arguments: [resource.name, cmdName, appHost.appHostPath, cmd],
                }));
            }
        }
    }

    dispose(): void {
        this._disposables.forEach(d => d.dispose());
        this._onDidChangeCodeLenses.dispose();
    }
}

function getEnabledCommand(commands: Record<string, ResourceCommandJson>, ...commandNames: string[]): ResourceCommandJson | undefined {
    return commandNames
        .map(commandName => commands[commandName])
        .find(command => isEnabledCommand(command) && isCommandVisibleToUi(command));
}

export function getCodeLensStateLabel(state: string, stateStyle: string, exitCode?: number | null): string {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
            if (stateStyle === StateStyle.Error) {
                return codeLensResourceRunningError;
            }
            if (stateStyle === StateStyle.Warning) {
                return codeLensResourceRunningWarning;
            }
            return codeLensResourceRunning;
        case ResourceState.Starting:
        case ResourceState.Building:
            return codeLensResourceStarting;
        case ResourceState.Waiting:
            return codeLensResourceWaiting;
        case ResourceState.NotStarted:
            return codeLensResourceNotStarted;
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
            return codeLensResourceError;
        case ResourceState.Stopping:
            return codeLensResourceStopping;
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopped:
            if (stateStyle === StateStyle.Error) {
                return exitCode != null && exitCode !== 0 ? codeLensResourceStoppedErrorWithExitCode(exitCode) : codeLensResourceStoppedError;
            }
            return exitCode != null && exitCode !== 0 ? codeLensResourceStoppedWithExitCode(exitCode) : codeLensResourceStopped;
        case ResourceState.ValueMissing:
            return codeLensResourceValueMissing;
        default:
            return state || codeLensResourceStopped;
    }
}

function getNormalizedCommandText(value: string | null | undefined): string | undefined {
    const normalized = value?.trim();
    return normalized ? normalized : undefined;
}
