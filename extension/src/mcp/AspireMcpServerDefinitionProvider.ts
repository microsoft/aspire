import * as vscode from 'vscode';
import {
    onDidChangeConfiguredCliPathRejection,
    onDidChangeResolvedCliPathForForwarding,
    resolveCliPath,
} from '../utils/cliPath';
import { extensionLogOutputChannel } from '../utils/logging';
import { getCmdShimSpawnCommandWithoutVerbatimArguments, shouldWrapWithCmd } from '../utils/cmdShim';
import { getRegisterMcpServerInWorkspace, registerMcpServerInWorkspaceSetting } from '../utils/settings';

const mcpServerLabel = 'Aspire';
const mcpServerArgs = ['agent', 'mcp'];
const aspireCliExecutablePathSetting = 'aspire.aspireCliExecutablePath';

/**
 * Builds the stdio definition VS Code uses to launch `aspire agent mcp`.
 *
 * Supported VS Code versions quote a .cmd path only when it contains whitespace.
 * Route command shims through cmd.exe so metacharacters in a no-space path remain
 * literal. See:
 * https://github.com/microsoft/vscode/blob/1.102.3/src/vs/workbench/api/node/extHostMcpNode.ts#L141-L167
 */
export function createAspireMcpServerDefinition(cliPath: string): vscode.McpStdioServerDefinition {
    if (!shouldWrapWithCmd(cliPath)) {
        return new vscode.McpStdioServerDefinition(mcpServerLabel, cliPath, [...mcpServerArgs]);
    }

    const { command, args } = getCmdShimSpawnCommandWithoutVerbatimArguments(cliPath, mcpServerArgs);
    return new vscode.McpStdioServerDefinition(mcpServerLabel, command, args);
}

/**
 * Provides the Aspire MCP server definition to VS Code so it appears
 * automatically in the MCP tools list when the Aspire CLI is available
 * and the workspace contains an Aspire project.
 */
export class AspireMcpServerDefinitionProvider implements vscode.McpServerDefinitionProvider<vscode.McpStdioServerDefinition> {
    private readonly _onDidChange = new vscode.EventEmitter<void>();
    readonly onDidChangeMcpServerDefinitions = this._onDidChange.event;

    private _cliPath: string | undefined;
    private _cliAvailable: boolean = false;
    private _shouldProvide: boolean = false;
    private _refreshGeneration = 0;
    private _configChangeDisposable: vscode.Disposable | undefined;
    private _workspaceFolderChangeDisposable: vscode.Disposable | undefined;
    private _cliPathForwardingChangeDisposable: vscode.Disposable | undefined;

    constructor() {
        // Re-evaluate when the setting changes
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration(registerMcpServerInWorkspaceSetting)
                || e.affectsConfiguration(aspireCliExecutablePathSetting)) {
                this.refresh();
            }
        });

        // Re-evaluate when workspace folders change
        this._workspaceFolderChangeDisposable = vscode.workspace.onDidChangeWorkspaceFolders(() => {
            this.refresh();
        });

        // Another CLI consumer can discover that the configured path stopped
        // working or that an unpersisted fallback changed. Re-resolve the MCP
        // command so it cannot keep serving the stale path.
        this._cliPathForwardingChangeDisposable = vscode.Disposable.from(
            onDidChangeConfiguredCliPathRejection(() => this.refresh()),
            onDidChangeResolvedCliPathForForwarding(() => this.refresh()),
        );
    }

    async refresh(): Promise<void> {
        const refreshGeneration = ++this._refreshGeneration;
        const [cliResult, shouldProvide] = await Promise.all([
            resolveCliPath(),
            checkShouldProvideMcpServer(),
        ]);

        if (refreshGeneration !== this._refreshGeneration) {
            return;
        }

        const changed =
            this._cliAvailable !== cliResult.available ||
            this._cliPath !== cliResult.cliPath ||
            this._shouldProvide !== shouldProvide;

        this._cliAvailable = cliResult.available;
        this._cliPath = cliResult.cliPath;
        this._shouldProvide = shouldProvide;

        if (changed) {
            extensionLogOutputChannel.info(`Aspire MCP server definition changed: cliAvailable=${cliResult.available}, shouldProvide=${shouldProvide}`);
            this._onDidChange.fire();
        }
    }

    provideMcpServerDefinitions(_token: vscode.CancellationToken): vscode.ProviderResult<vscode.McpStdioServerDefinition[]> {
        if (!this._cliAvailable || !this._shouldProvide || !this._cliPath) {
            return [];
        }

        return [createAspireMcpServerDefinition(this._cliPath)];
    }

    dispose(): void {
        this._refreshGeneration++;
        this._configChangeDisposable?.dispose();
        this._workspaceFolderChangeDisposable?.dispose();
        this._cliPathForwardingChangeDisposable?.dispose();
        this._onDidChange.dispose();
    }
}

/**
 * Determines whether the Aspire MCP server should be provided.
 *
 * The server is provided only when workspace folders are open and the
 * "aspire.registerMcpServerInWorkspace" setting is enabled.
 */
async function checkShouldProvideMcpServer(): Promise<boolean> {
    if (!vscode.workspace.workspaceFolders || vscode.workspace.workspaceFolders.length === 0) {
        return false;
    }

    return getRegisterMcpServerInWorkspace();
}
