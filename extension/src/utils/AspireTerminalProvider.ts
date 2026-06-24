import * as vscode from 'vscode';
import * as childProcess from 'child_process';
import { aspireTerminalName, dcpServerNotInitialized, rpcServerNotInitialized, terminalCommandArgumentControlCharacters, terminalCommandUnsafeLiteral } from '../loc/strings';
import { extensionLogOutputChannel } from './logging';
import { RpcServerConnectionInfo } from '../server/AspireRpcServer';
import { DcpServerConnectionInfo } from '../dcp/types';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { EnvironmentVariables, getEnvironmentWithoutE2EBridgeVariables } from './environment';
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
    terminalTarget?: 'shared' | 'editor';
}

// String parts are fixed CLI syntax and are validated before interpolation.
// ShellArg parts are workspace/user data that must be shell-quoted at the
// terminal boundary.
export interface ShellArg {
    readonly quote: true;
    readonly value: string;
}

export type AspireSubcommand = string | readonly (string | ShellArg)[];

export interface AspireTerminalCommandEvent {
    subcommand: string;
    commandLine: string;
    showTerminal: boolean;
    additionalArgs?: readonly string[];
    containsRedactedArgs: boolean;
    executionSuppressed: boolean;
    executionMode: 'suppressed' | 'shellIntegration' | 'sendText';
}

/**
 * Quotes a single argument for safe interpolation into a shell command line.
 *
 * Windows: The output targets the PowerShell terminal created by getAspireTerminal().
 * The terminal prefers PowerShell 7 (pwsh.exe) and falls back to Windows PowerShell
 * (powershell.exe). The argument is wrapped in double quotes and the
 * interpolation-significant characters (backtick, PowerShell quote delimiters,
 * dollar sign) are backtick-escaped, which both shells use for expandable
 * strings. PowerShell treats smart quotes as quote delimiters too:
 * https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_quoting_rules
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
    assertNoTerminalControlCharacters(arg);

    if (platform === 'win32') {
        const escaped = arg.replace(/[`"$\u2018\u2019\u201C\u201D]/g, value => value === '`' ? '``' : '`' + value);
        return `"${escaped}"`;
    }

    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
}

export function shellArg(value: string): ShellArg {
    return { quote: true, value };
}

export class AspireTerminalProvider implements vscode.Disposable {
    private _terminalByDebugSessionId: Map<string | null, AspireTerminal> = new Map();
    private _rpcServerConnectionInfo?: RpcServerConnectionInfo;
    private _dcpServerConnectionInfo?: DcpServerConnectionInfo;
    private _windowsPowerShellPath?: string;

    private readonly _onDidSendAspireCommand = new vscode.EventEmitter<AspireTerminalCommandEvent>();
    readonly onDidSendAspireCommand = this._onDidSendAspireCommand.event;

    constructor(
        subscriptions: vscode.Disposable[],
        private readonly _isPowerShell7Available = isPowerShell7Available,
    ) {
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

    async sendAspireCommandToAspireTerminal(subcommand: AspireSubcommand, showTerminal: boolean = true, additionalArgs?: string[], options?: SendAspireCommandOptions) {
        const cliPath = await this.getAspireCliExecutablePath();
        const subcommandLine = formatSubcommand(subcommand);
        assertNoTerminalControlCharacters(cliPath);

        // On Windows, use & to execute paths, especially those with special characters
        // On Unix, just use the path directly
        let command: string;
        if (process.platform === 'win32') {
            command = `& ${quoteShellArg(cliPath)} ${subcommandLine}`;
        } else {
            // For Unix-like systems, quote only if needed
            const quotedPath = /[\s"'`$!*?()&|<>;]/.test(cliPath) ? `'${cliPath.replace(/'/g, `'\"'\"'`)}'` : cliPath;
            command = `${quotedPath} ${subcommandLine}`;
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
        assertNoTerminalControlCharacters(command);

        let logCommand = command;
        if (options?.redactAdditionalArgs && additionalArgs && additionalArgs.length > 0) {
            const logArgs = extensionArgs.map(arg => quoteShellArg(arg));
            logArgs.push('[redacted command arguments]');
            logCommand = `${baseCommand} ${logArgs.join(' ')}`;
        }
        const executionSuppressed = isE2eTerminalCommandExecutionSuppressed();
        const terminalTarget = options?.terminalTarget ?? 'shared';
        let aspireTerminal: AspireTerminal | undefined;
        let executionMode: AspireTerminalCommandEvent['executionMode'];
        if (executionSuppressed) {
            executionMode = 'suppressed';
        }
        else {
            aspireTerminal = terminalTarget === 'editor'
                ? this.createAspireEditorTerminal()
                : this.getAspireTerminal();
            executionMode = aspireTerminal.terminal.shellIntegration ? 'shellIntegration' : 'sendText';
        }
        this._onDidSendAspireCommand.fire({
            subcommand: subcommandLine,
            commandLine: logCommand,
            showTerminal,
            additionalArgs: options?.redactAdditionalArgs ? undefined : cliArgs,
            containsRedactedArgs: options?.redactAdditionalArgs === true && additionalArgs !== undefined && additionalArgs.length > 0,
            executionSuppressed,
            executionMode,
        });
        extensionLogOutputChannel.info(`Sending command to Aspire terminal: ${logCommand}`);

        if (executionSuppressed) {
            return;
        }

        if (!aspireTerminal) {
            throw new Error('Aspire terminal was not created for an unsuppressed command.');
        }

        if (showTerminal) {
            aspireTerminal.terminal.show();
        }

        if (executionMode === 'shellIntegration' && aspireTerminal.terminal.shellIntegration) {
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
        const terminal = this.createTerminal();

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

    private createAspireEditorTerminal(): AspireTerminal {
        extensionLogOutputChannel.info('Creating Aspire editor terminal');
        const terminal = this.createTerminal(vscode.TerminalLocation.Editor);
        return {
            terminal,
            dispose: () => terminal.dispose(),
        };
    }

    private createTerminal(location?: vscode.TerminalLocation): vscode.Terminal {
        const terminalOptions: vscode.TerminalOptions = {
            name: aspireTerminalName,
            env: this.createEnvironment(),
            location,
        };
        if (process.platform === 'win32') {
            // quoteShellArg uses PowerShell escaping on Windows. Do not rely on the
            // user's default terminal profile because cmd.exe treats backticks as
            // ordinary characters and would make quoted values containing " shell-sensitive again.
            terminalOptions.shellPath = this.getWindowsPowerShellPath();
        }

        return vscode.window.createTerminal(terminalOptions);
    }

    createEnvironment(debugSessionId?: string, noDebug?: boolean, noExtensionVariables?: boolean): any {
        if (noExtensionVariables) {
            return getEnvironmentWithoutE2EBridgeVariables();
        }

        const env: any = {
            ...getEnvironmentWithoutE2EBridgeVariables(),

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
            this.addDcpRunSessionEnvironment(env, debugSessionId, noDebug);
        }

        return env;
    }

    createDcpRunSessionEnvironment(debugSessionId: string, noDebug?: boolean): any {
        const env: any = {
            ...getEnvironmentWithoutE2EBridgeVariables(),

            // Include DCP server info without the extension RPC backchannel. Short-lived
            // helper CLI processes must not register an extension backchannel because the
            // CLI's ProcessExit hook stops the debug session attached to that backchannel.
            DEBUG_SESSION_PORT: this.dcpServerConnectionInfo.address,
            DEBUG_SESSION_TOKEN: this.dcpServerConnectionInfo.token,
            DEBUG_SESSION_SERVER_CERTIFICATE: this.dcpServerConnectionInfo.certificate,
        };

        delete env.ASPIRE_EXTENSION_ENDPOINT;
        delete env.ASPIRE_EXTENSION_TOKEN;
        delete env.ASPIRE_EXTENSION_CERT;

        this.addDcpRunSessionEnvironment(env, debugSessionId, noDebug);

        return env;
    }

    private addDcpRunSessionEnvironment(env: any, debugSessionId: string, noDebug?: boolean): void {
        env.ASPIRE_EXTENSION_DEBUG_SESSION_ID = debugSessionId;
        env.DCP_INSTANCE_ID_PREFIX = debugSessionId + '-';
        env.DEBUG_SESSION_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
        env.ASPIRE_EXTENSION_DEBUG_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
        env.DEBUG_SESSION_INFO = JSON.stringify(getRunSessionInfo());
        env.ASPIRE_EXTENSION_CAPABILITIES = getSupportedCapabilities().join(',');
        // Extension-managed debug/run sessions stream CLI output into VS Code's
        // debug console, which is not an interactive terminal. Keep prompts routed
        // through the extension backchannel while disabling Spectre live output
        // such as the first-run banner and spinners.
        env.ASPIRE_NON_INTERACTIVE = 'true';

        // While debugging, the developer can pause on a breakpoint (e.g. before builder.Build())
        // for an arbitrarily long time. Use a very long startup timeout (86400s = 24h) so the parent
        // Aspire CLI doesn't hit its normal ~120s startup timeout and tear down the debug session.
        // An explicitly configured ASPIRE_CLI_START_TIMEOUT still wins.
        if (noDebug === false && !hasConfiguredEnvironmentVariable(env, EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT)) {
            env[EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT] = '86400';
        }

        // if DCP debug logging is enabled, set DCP-specific logging environment variables
        const dcpDebugLoggingEnabled = vscode.workspace.getConfiguration('aspire').get<boolean>('enableAspireDcpDebugLogging', false);
        const workspaceRoot = vscode.workspace.workspaceFolders?.[0];
        if (dcpDebugLoggingEnabled && workspaceRoot) {
            env.DCP_DIAGNOSTICS_LOG_LEVEL = "debug";
            env.DCP_PRESERVE_EXECUTABLE_LOGS = "1";
            env.DCP_DIAGNOSTICS_LOG_FOLDER = path.join(workspaceRoot.uri.fsPath, '.aspire', 'dcp', `logs-${debugSessionId}`);
        }
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
        this._onDidSendAspireCommand.dispose();
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

    private getWindowsPowerShellPath(): string {
        if (this._windowsPowerShellPath !== undefined) {
            return this._windowsPowerShellPath;
        }

        this._windowsPowerShellPath = this._isPowerShell7Available()
            ? 'pwsh.exe'
            : 'powershell.exe';

        return this._windowsPowerShellPath;
    }
}

function isPowerShell7Available(): boolean {
    const result = childProcess.spawnSync('pwsh.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], {
        stdio: 'ignore',
        windowsHide: true,
    });

    return result.status === 0 && result.error === undefined;
}

function hasConfiguredEnvironmentVariable(env: Record<string, string | undefined>, name: string): boolean {
    if (env[name]) {
        return true;
    }

    if (process.platform !== 'win32') {
        return false;
    }

    // Windows environment variables are case-insensitive. Avoid adding a second
    // differently-cased key because Node picks only one when spawning the child process.
    return Object.entries(env).some(([key, value]) => key.toUpperCase() === name && !!value);
}

function isE2eTerminalCommandExecutionSuppressed(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
        !!process.env.ASPIRE_EXTENSION_E2E_STATE_FILE &&
        !!process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE &&
        process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION === 'true';
}

function assertNoTerminalControlCharacters(value: string): void {
    // Shell quoting protects shell metacharacters after the command reaches the
    // shell. C0 controls are terminal input first: in sendText fallback, ETX can
    // abort the current line and CR/LF can submit following text as another
    // command before shell parsing can make those bytes inert. Tab is allowed
    // because shells treat it as ordinary whitespace inside quotes.
    if (/[\x00-\x08\x0A-\x1F\x7F]/.test(value)) {
        throw new Error(terminalCommandArgumentControlCharacters);
    }
}

function validateLiteralSubcommandPart(value: string): string {
    if (!/^-{0,2}[A-Za-z0-9][-A-Za-z0-9]*$/.test(value)) {
        throw new Error(terminalCommandUnsafeLiteral);
    }

    return value;
}

function formatSubcommand(subcommand: AspireSubcommand): string {
    if (typeof subcommand === 'string') {
        return subcommand;
    }

    return subcommand.map(part => typeof part === 'string' ? validateLiteralSubcommandPart(part) : quoteShellArg(part.value)).join(' ');
}
