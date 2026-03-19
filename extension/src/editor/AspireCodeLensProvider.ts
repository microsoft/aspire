import * as vscode from 'vscode';
import { getParserForDocument } from './parsers/AppHostResourceParser';
// Import parsers to trigger self-registration
import './parsers/csharpAppHostParser';
import './parsers/jsTsAppHostParser';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { ResourceJson, AppHostDisplayInfo, ResourceCommandJson } from '../views/AppHostDataRepository';
import { findResourceState, findWorkspaceResourceState } from './resourceStateUtils';
import { ResourceState, HealthStatus, StateStyle, ResourceType } from './resourceConstants';
import {
    codeLensDebugPipelineStep,
    codeLensResourceRunning,
    codeLensResourceRunningWarning,
    codeLensResourceRunningError,
    codeLensResourceStarting,
    codeLensResourceStopped,
    codeLensResourceStoppedError,
    codeLensResourceError,
    codeLensRestart,
    codeLensStop,
    codeLensStart,
    codeLensViewLogs,
    codeLensCommand,
} from '../loc/strings';

export class AspireCodeLensProvider implements vscode.CodeLensProvider {
    private readonly _onDidChangeCodeLenses = new vscode.EventEmitter<void>();
    readonly onDidChangeCodeLenses = this._onDidChangeCodeLenses.event;

    private _disposables: vscode.Disposable[] = [];

    constructor(private readonly _treeProvider: AspireAppHostTreeProvider) {
        // Re-compute lenses whenever the polling data changes
        this._disposables.push(
            _treeProvider.onDidChangeTreeData(() => this._onDidChangeCodeLenses.fire())
        );
    }

    provideCodeLenses(document: vscode.TextDocument, _token: vscode.CancellationToken): vscode.CodeLens[] {
        if (!vscode.workspace.getConfiguration('aspire').get<boolean>('enableCodeLens', true)) {
            return [];
        }

        const parser = getParserForDocument(document);
        if (!parser) {
            return [];
        }

        const resources = parser.parseResources(document);
        if (resources.length === 0) {
            return [];
        }

        const appHosts = this._treeProvider.appHosts;
        const workspaceResources = this._treeProvider.workspaceResources;
        const workspaceAppHostPath = this._treeProvider.workspaceAppHostPath ?? '';
        const hasRunningData = appHosts.length > 0 || workspaceResources.length > 0;
        const findWorkspace = findWorkspaceResourceState(workspaceResources, workspaceAppHostPath);

        const lenses: vscode.CodeLens[] = [];

        for (const resource of resources) {
            // Use statementStartLine to position the CodeLens at the top of a multi-line chain
            const lensLine = resource.statementStartLine ?? resource.range.start.line;
            const lineRange = new vscode.Range(lensLine, 0, lensLine, 0);

            if (resource.kind === 'pipelineStep') {
                // Pipeline steps get Debug lens when no AppHost is running
                if (!hasRunningData) {
                    this._addPipelineStepLenses(lenses, lineRange, resource.name);
                }
            } else if (resource.kind === 'resource') {
                // Resources get state lenses when live data is available
                if (hasRunningData) {
                    const match = findResourceState(appHosts, resource.name)
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

    private _addStateLenses(
        lenses: vscode.CodeLens[],
        range: vscode.Range,
        resource: ResourceJson,
        appHost: AppHostDisplayInfo,
    ): void {
        const state = resource.state ?? '';
        const stateStyle = resource.stateStyle ?? '';
        const healthStatus = resource.healthStatus;
        const commands = resource.commands ? Object.keys(resource.commands) : [];

        // State indicator lens (clickable — reveals resource in tree view)
        let stateLabel = getCodeLensStateLabel(state, stateStyle);
        if (healthStatus && healthStatus !== HealthStatus.Healthy) {
            stateLabel += ` - (${healthStatus})`;
        }
        lenses.push(new vscode.CodeLens(range, {
            title: stateLabel,
            command: 'aspire-vscode.codeLensRevealResource',
            tooltip: `${resource.displayName ?? resource.name}: ${state}${healthStatus ? ` (${healthStatus})` : ''}`,
            arguments: [resource.displayName ?? resource.name],
        }));

        // Action lenses based on available commands
        if (commands.includes('restart') || commands.includes('resource-restart')) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensRestart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensRestart,
                arguments: [resource.name, 'restart', appHost.appHostPath],
            }));
        }

        if (commands.includes('stop') || commands.includes('resource-stop')) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStop,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStop,
                arguments: [resource.name, 'stop', appHost.appHostPath],
            }));
        }

        if (commands.includes('start') || commands.includes('resource-start')) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStart,
                arguments: [resource.name, 'start', appHost.appHostPath],
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
        if (resource.commands) {
            for (const [cmdName, cmd] of Object.entries(resource.commands) as [string, ResourceCommandJson][]) {
                if (!standardCommands.has(cmdName)) {
                    const label = codeLensCommand(cmd.description ?? cmdName);
                    lenses.push(new vscode.CodeLens(range, {
                        title: label,
                        command: 'aspire-vscode.codeLensResourceAction',
                        tooltip: cmd.description ?? cmdName,
                        arguments: [resource.name, cmdName, appHost.appHostPath],
                    }));
                }
            }
        }
    }

    dispose(): void {
        this._disposables.forEach(d => d.dispose());
        this._onDidChangeCodeLenses.dispose();
    }
}

export function getCodeLensStateLabel(state: string, stateStyle: string): string {
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
        case ResourceState.Waiting:
        case ResourceState.NotStarted:
            return codeLensResourceStarting;
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
            return codeLensResourceError;
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopping:
            if (stateStyle === StateStyle.Error) {
                return codeLensResourceStoppedError;
            }
            return codeLensResourceStopped;
        default:
            return state || codeLensResourceStopped;
    }
}
