import * as vscode from 'vscode';
import { CliPathResolver, cliPathResolver } from '../utils/cliPath';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { extensionLogOutputChannel } from '../utils/logging';
import { getCmdShimSpawnCommandWithoutVerbatimArguments, shouldWrapWithCmd } from '../utils/cmdShim';
import { getRegisterMcpServerInWorkspace, registerMcpServerInWorkspaceSetting } from '../utils/settings';
import { ASPIRE_CLI_PATH_ENV_VAR, getForwardableResolvedAspireCliPath, ResolvedCliPathDependencies } from '../utils/cliPathEnvironment';

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
export function createAspireMcpServerDefinition(
    cliPath: string,
    label = mcpServerLabel,
    cwd?: vscode.Uri,
    deps?: ResolvedCliPathDependencies,
): vscode.McpStdioServerDefinition {
    // `aspire agent mcp` can build an AppHost, and that build inherits this environment. An
    // unbundled framework-dependent CLI path makes MSBuild's ResolveAspireCliBundle bind bundle
    // assets to a CLI that has no bundle layout (ASPIRE009), so it must not be forwarded. Every
    // other AspireCliPath producer applies the same guard; omitting the variable lets the build
    // fall back to PATH probing, exactly as those sites do.
    const forwardableCliPath = deps === undefined
        ? getForwardableResolvedAspireCliPath(cliPath)
        : getForwardableResolvedAspireCliPath(cliPath, deps);
    const env = forwardableCliPath === undefined ? undefined : { [ASPIRE_CLI_PATH_ENV_VAR]: forwardableCliPath };
    let definition: vscode.McpStdioServerDefinition;
    if (!shouldWrapWithCmd(cliPath)) {
        definition = new vscode.McpStdioServerDefinition(label, cliPath, [...mcpServerArgs], env);
    }
    else {
        const { command, args } = getCmdShimSpawnCommandWithoutVerbatimArguments(cliPath, mcpServerArgs);
        definition = new vscode.McpStdioServerDefinition(label, command, args, env);
    }
    definition.cwd = cwd;
    return definition;
}

/**
 * Provides the Aspire MCP server definition to VS Code so it appears
 * automatically in the MCP tools list when the Aspire CLI is available
 * and the workspace contains an Aspire project.
 */
export class AspireMcpServerDefinitionProvider implements vscode.McpServerDefinitionProvider<vscode.McpStdioServerDefinition> {
    private readonly _onDidChange = new vscode.EventEmitter<void>();
    readonly onDidChangeMcpServerDefinitions = this._onDidChange.event;

    private _definitions: vscode.McpStdioServerDefinition[] = [];
    private _refreshGeneration = 0;
    private _configChangeDisposable: vscode.Disposable | undefined;
    private _workspaceFolderChangeDisposable: vscode.Disposable | undefined;
    private _workspaceTrustGrantDisposable: vscode.Disposable | undefined;
    private _cliPathForwardingChangeDisposable: vscode.Disposable | undefined;

    constructor(private readonly _resolver: CliPathResolver = cliPathResolver) {
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

        this._workspaceTrustGrantDisposable = vscode.workspace.onDidGrantWorkspaceTrust(() => {
            this.refresh();
        });

        // Another CLI consumer can discover that the configured path stopped
        // working or that an unpersisted fallback changed. Re-resolve the MCP
        // command so it cannot keep serving the stale path.
        this._cliPathForwardingChangeDisposable = this._resolver.onDidChangeForwarding(() => this.refresh());
    }

    async refresh(): Promise<void> {
        const refreshGeneration = ++this._refreshGeneration;
        const workspaceFolders = vscode.workspace.workspaceFolders ?? [];
        const shouldProvide = await checkShouldProvideMcpServer();
        const cliResults = shouldProvide
            ? await Promise.all(workspaceFolders.map(folder => this._resolver.resolve(workspaceFolderCliPathTarget(folder))))
            : [];

        if (refreshGeneration !== this._refreshGeneration) {
            return;
        }

        const folderNameCounts = new Map<string, number>();
        for (const folder of workspaceFolders) {
            folderNameCounts.set(folder.name, (folderNameCounts.get(folder.name) ?? 0) + 1);
        }
        const reservedFolderLabels = new Set(workspaceFolders.map(folder => folder.name));
        const allocatedFolderLabels = new Set<string>();
        const folderNameOrdinals = new Map<string, number>();
        const folderLabels = workspaceFolders.map(folder => {
            let folderLabel = folder.name;
            if ((folderNameCounts.get(folder.name) ?? 0) > 1) {
                let ordinal = folderNameOrdinals.get(folder.name) ?? 0;
                do {
                    ordinal++;
                    folderLabel = `${folder.name} ${ordinal}`;
                } while (reservedFolderLabels.has(folderLabel) || allocatedFolderLabels.has(folderLabel));
                folderNameOrdinals.set(folder.name, ordinal);
            }
            allocatedFolderLabels.add(folderLabel);
            return folderLabel;
        });
        const definitions = cliResults.flatMap((result, index) => {
            if (!result.available) {
                return [];
            }

            const folder = workspaceFolders[index];
            const label = workspaceFolders.length === 1 ? mcpServerLabel : `${mcpServerLabel} (${folderLabels[index]})`;
            return [createAspireMcpServerDefinition(result.cliPath, label, folder.uri)];
        });
        const changed = !areMcpDefinitionsEqual(this._definitions, definitions);
        this._definitions = definitions;

        if (changed) {
            extensionLogOutputChannel.info(`Aspire MCP server definitions changed: count=${definitions.length}, shouldProvide=${shouldProvide}`);
            this._onDidChange.fire();
        }
    }

    provideMcpServerDefinitions(_token: vscode.CancellationToken): vscode.ProviderResult<vscode.McpStdioServerDefinition[]> {
        return [...this._definitions];
    }

    dispose(): void {
        this._refreshGeneration++;
        this._configChangeDisposable?.dispose();
        this._workspaceFolderChangeDisposable?.dispose();
        this._workspaceTrustGrantDisposable?.dispose();
        this._cliPathForwardingChangeDisposable?.dispose();
        this._onDidChange.dispose();
    }
}

function areMcpDefinitionsEqual(
    left: readonly vscode.McpStdioServerDefinition[],
    right: readonly vscode.McpStdioServerDefinition[],
): boolean {
    return left.length === right.length && left.every((definition, index) => {
        const other = right[index];
        return definition.label === other.label
            && definition.command === other.command
            && definition.cwd?.toString() === other.cwd?.toString()
            && definition.args.length === other.args.length
            && definition.args.every((argument, argumentIndex) => argument === other.args[argumentIndex])
            && JSON.stringify(definition.env) === JSON.stringify(other.env);
    });
}

/**
 * Determines whether the Aspire MCP server should be provided.
 *
 * The server is provided only when workspace folders are open and the
 * "aspire.registerMcpServerInWorkspace" setting is enabled.
 */
async function checkShouldProvideMcpServer(): Promise<boolean> {
    if (!vscode.workspace.isTrusted) {
        return false;
    }

    if (!vscode.workspace.workspaceFolders || vscode.workspace.workspaceFolders.length === 0) {
        return false;
    }

    return getRegisterMcpServerInWorkspace();
}
