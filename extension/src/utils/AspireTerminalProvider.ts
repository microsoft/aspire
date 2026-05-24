import * as vscode from 'vscode';
import { aspireTerminalName, dcpServerNotInitialized, rpcServerNotInitialized } from '../loc/strings';
import { extensionLogOutputChannel } from './logging';
import { RpcServerConnectionInfo } from '../server/AspireRpcServer';
import { DcpServerConnectionInfo } from '../dcp/types';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { EnvironmentVariables } from './environment';
import { resolveCliPath } from './cliPath';
import path from 'path';

export const enum AnsiColors {
    Green = '\x1b[32m',
    Yellow = '\x1b[33m',
    Blue = '\x1b[34m',
}

export interface AspireTerminal {
    terminal: vscode.Terminal;
    dispose: () => void;
}

export interface SendAspireCommandOptions {
    redactAdditionalArgs?: boolean;
}

/**
 * Quotes a single argument for safe interpolation into a shell command line.
 *
 * Windows: The output targets PowerShell (powershell.exe / pwsh.exe), which is
 * VS Code's default integrated terminal on Windows. The argument is wrapped in
 * double quotes and the interpolation-significant characters (backtick, double
 * quote, dollar sign) are backtick-escaped. This form is NOT safe for cmd.exe;
 * users who have configured cmd.exe as their default terminal may see
 * unexpected behavior. End-to-end coverage through a real child process is
 * out of scope for this helper.
 *
 * Unix: The output uses POSIX single-quote quoting, which is interpreted the
 * same way by bash, zsh, dash, sh, and fish. Embedded single quotes are split
 * out and rejoined with a double-quoted single quote.
 *
 * @param arg The raw argument value to quote.
 * @param platform Override for the target platform. Defaults to
 * `process.platform`, but tests pass an explicit value to validate both
 * branches regardless of the host OS.
 */
export function quoteShellArg(arg: string, platform: NodeJS.Platform = process.platform): string {
    if (platform === 'win32') {
        // Order matters: escape backticks first so that the backticks we
        // introduce when escaping " and $ are not themselves re-escaped.
        return `"${arg.replace(/`/g, '``').replace(/"/g, '`"').replace(/\$/g, '`$')}"`;
    }

    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
}

export class AspireTerminalProvider implements vscode.Disposable {
    private _terminalByDebugSessionId: Map<string | null, AspireTerminal> = new Map();
    private _rpcServerConnectionInfo?: RpcServerConnectionInfo;
    private _dcpServerConnectionInfo?: DcpServerConnectionInfo;

    constructor(subscriptions: vscode.Disposable[]) {
        subscriptions.push(vscode.window.onDidCloseTerminal(closedTerminal => {
            for (const [debugSessionId, terminal] of this._terminalByDebugSessionId.entries()) {
                if (terminal.terminal === closedTerminal) {
                    this._terminalByDebugSessionId.delete(debugSessionId);
                    break;
                }
            }
        }));
    }

    get rpcServerConnectionInfo() {
        if (!this._rpcServerConnectionInfo) {
            throw new Error(rpcServerNotInitialized);
        }

        return this._rpcServerConnectionInfo;
    }

    set rpcServerConnectionInfo(value: RpcServerConnectionInfo) {
        this._rpcServerConnectionInfo = value;
    }

    get dcpServerConnectionInfo() {
        if (!this._dcpServerConnectionInfo) {
            throw new Error(dcpServerNotInitialized);
        }

        return this._dcpServerConnectionInfo;
    }

    set dcpServerConnectionInfo(value: DcpServerConnectionInfo) {
        this._dcpServerConnectionInfo = value;
    }

    async sendAspireCommandToAspireTerminal(subcommand: string, showTerminal: boolean = true, additionalArgs?: string[], options?: SendAspireCommandOptions) {
        const cliPath = await this.getAspireCliExecutablePath();

        // On Windows, use & to execute paths, especially those with special characters
        // On Unix, just use the path directly
        let command: string;
        if (process.platform === 'win32') {
            // Use & call operator with quoted path for Windows
            command = `& "${cliPath}" ${subcommand}`;
        } else {
            // For Unix-like systems, quote only if needed
            const quotedPath = /[\s"'`$!*?()&|<>;]/.test(cliPath) ? `'${cliPath.replace(/'/g, `'\"'\"'`)}'` : cliPath;
            command = `${quotedPath} ${subcommand}`;
        }
        const baseCommand = command;

        const extensionArgs: string[] = [];
        if (this.isCliDebugLoggingEnabled()) {
            extensionArgs.push('--debug');
        }

        if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
            extensionArgs.push('--cli-wait-for-debugger');
        }

        const cliArgs = additionalArgs && additionalArgs.length > 0
            ? [...extensionArgs, ...additionalArgs]
            : extensionArgs;

        if (cliArgs.length > 0) {
            const quotedArgs = cliArgs.map(arg => quoteShellArg(arg));
            command += ' ' + quotedArgs.join(' ');
        }

        const aspireTerminal = this.getAspireTerminal();
        let logCommand = command;
        if (options?.redactAdditionalArgs && additionalArgs && additionalArgs.length > 0) {
            const logArgs = extensionArgs.map(arg => quoteShellArg(arg));
            logArgs.push('[redacted command arguments]');
            logCommand = `${baseCommand} ${logArgs.join(' ')}`;
        }
        extensionLogOutputChannel.info(`Sending command to Aspire terminal: ${logCommand}`);

        if (showTerminal) {
            aspireTerminal.terminal.show();
        }

        if (aspireTerminal.terminal.shellIntegration) {
            aspireTerminal.terminal.shellIntegration.executeCommand(command);
        }
        else {
            // Without shell integration, VS Code can't tell whether the terminal is idle or
            // a foreground process is running, so keep the previous safe interruption behavior.
            aspireTerminal.terminal.sendText('\x03', false);
            aspireTerminal.terminal.sendText(command);
        }

    }

    getAspireTerminal(forceCreate?: boolean): AspireTerminal {
        const terminalName = aspireTerminalName;

        const existingTerminal = this._terminalByDebugSessionId.get(null);
        if (existingTerminal) {
            if (!forceCreate) {
                return existingTerminal;
            }
            else {
                existingTerminal.dispose();
            }
        }

        extensionLogOutputChannel.info(`Creating new Aspire terminal`);
        const terminal = vscode.window.createTerminal({
            name: terminalName,
            env: this.createEnvironment(),
        });

        const aspireTerminal: AspireTerminal = {
            terminal,
            dispose: () => {
                terminal.dispose();
                this._terminalByDebugSessionId.delete(null);
            }
        };

        this._terminalByDebugSessionId.set(null, aspireTerminal);

        return aspireTerminal;
    }

    createEnvironment(debugSessionId?: string, noDebug?: boolean, noExtensionVariables?: boolean): any {
        if (noExtensionVariables) {
            return process.env;
        }

        const env: any = {
            ...process.env,

            // Extension connection information
            ASPIRE_EXTENSION_ENDPOINT: this.rpcServerConnectionInfo.address,
            ASPIRE_EXTENSION_TOKEN: this.rpcServerConnectionInfo.token,
            ASPIRE_EXTENSION_CERT: Buffer.from(this.rpcServerConnectionInfo.cert, 'utf-8').toString('base64'),
            ASPIRE_EXTENSION_PROMPT_ENABLED: 'true',

            // Use the current locale in the CLI
            ASPIRE_LOCALE_OVERRIDE: vscode.env.language,

            // Include DCP server info
            DEBUG_SESSION_PORT: this.dcpServerConnectionInfo.address,
            DEBUG_SESSION_TOKEN: this.dcpServerConnectionInfo.token,
            DEBUG_SESSION_SERVER_CERTIFICATE: this.dcpServerConnectionInfo.certificate,
        };

        if (debugSessionId) {
            env.ASPIRE_EXTENSION_DEBUG_SESSION_ID = debugSessionId;
            env.DCP_INSTANCE_ID_PREFIX = debugSessionId + '-';
            env.DEBUG_SESSION_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
            env.ASPIRE_EXTENSION_DEBUG_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
            env.DEBUG_SESSION_INFO = JSON.stringify(getRunSessionInfo());
            env.ASPIRE_EXTENSION_CAPABILITIES = getSupportedCapabilities().join(',');

            // if DCP debug logging is enabled, set DCP-specific logging environment variables
            const dcpDebugLoggingEnabled = vscode.workspace.getConfiguration('aspire').get<boolean>('enableAspireDcpDebugLogging', false);
            const workspaceRoot = vscode.workspace.workspaceFolders?.[0];
            if (dcpDebugLoggingEnabled && workspaceRoot) {
                env.DCP_DIAGNOSTICS_LOG_LEVEL = "debug";
                env.DCP_PRESERVE_EXECUTABLE_LOGS = "1";
                env.DCP_DIAGNOSTICS_LOG_FOLDER = path.join(workspaceRoot.uri.fsPath, '.aspire', 'dcp', `logs-${debugSessionId}`);
            }
        }

        return env;
    }

    closeAllOpenAspireTerminals() {
        extensionLogOutputChannel.info('Closing all open Aspire terminals');

        // First, dispose any terminals we are explicitly tracking
        for (const [debugSessionId, aspireTerminal] of this._terminalByDebugSessionId.entries()) {
            try {
                aspireTerminal.terminal.dispose();
            }
            catch (err) {
                extensionLogOutputChannel.error(`Failed to dispose Aspire terminal for session ${debugSessionId}: ${err}`);
            }
        }

        // Also dispose any terminals left over from previous runs that we didn't track
        for (const term of vscode.window.terminals) {
            try {
                if (term.name === aspireTerminalName) {
                    extensionLogOutputChannel.info(`Disposing unregistered Aspire terminal: ${term.name}`);
                    term.dispose();
                }
            }
            catch (err) {
                extensionLogOutputChannel.error(`Failed to dispose unregistered Aspire terminal ${term.name}: ${err}`);
            }
        }

        this._terminalByDebugSessionId.clear();
    }

    dispose() {
        for (const terminal of this._terminalByDebugSessionId.values()) {
            terminal.dispose();
        }
    }


    async getAspireCliExecutablePath(): Promise<string> {
        const result = await resolveCliPath();
        return result.cliPath;
    }

    isCliDebugLoggingEnabled(): boolean {
        return vscode.workspace.getConfiguration('aspire').get<boolean>('enableAspireCliDebugLogging', false);
    }

    isDebugConfigEnvironmentLoggingEnabled(): boolean {
        return vscode.workspace.getConfiguration('aspire').get<boolean>('enableDebugConfigEnvironmentLogging', false);
    }
}
