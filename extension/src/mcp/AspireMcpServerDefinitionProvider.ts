import * as vscode from 'vscode';
import { resolveCliPath } from '../utils/cliPath';
import { extensionLogOutputChannel } from '../utils/logging';
import { getRegisterMcpServerInWorkspace, registerMcpServerInWorkspaceSetting } from '../utils/settings';

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
    private _configChangeDisposable: vscode.Disposable | undefined;
    private _workspaceFolderChangeDisposable: vscode.Disposable | undefined;

    constructor() {
        // Re-evaluate when the setting changes
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration(registerMcpServerInWorkspaceSetting)) {
                this.refresh();
            }
        });

        // Re-evaluate when workspace folders change
        this._workspaceFolderChangeDisposable = vscode.workspace.onDidChangeWorkspaceFolders(() => {
            this.refresh();
        });
    }

    async refresh(): Promise<void> {
        const [cliResult, shouldProvide] = await Promise.all([
            resolveCliPath(),
            checkShouldProvideMcpServer(),
        ]);

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

        return [new vscode.McpStdioServerDefinition('Aspire', this._cliPath, ['agent', 'mcp'])];
    }

    dispose(): void {
        this._configChangeDisposable?.dispose();
        this._workspaceFolderChangeDisposable?.dispose();
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
