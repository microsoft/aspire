import * as vscode from 'vscode';
import * as childProcess from 'child_process';
import { aspireTerminalName, dcpServerNotInitialized, rpcServerNotInitialized, terminalCommandUnsafeLiteral } from '../loc/strings';
import { extensionLogOutputChannel } from './logging';
import { RpcServerConnectionInfo } from '../server/AspireRpcServer';
import { DcpServerConnectionInfo } from '../dcp/types';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { EnvironmentVariables, getEnvironmentWithoutE2EBridgeVariables } from './environment';
import { CliPathResolutionResult, CliPathResolver, resolveCliPath } from './cliPath';
import { ASPIRE_CLI_PATH_ENV_VAR, getForwardableAspireCliPath, getForwardableResolvedAspireCliPath } from './cliPathEnvironment';
import { CliPathResolutionTarget, getCliPathTargetKey, windowCliPathTarget } from './cliPathVariables';
import path from 'path';
import { assertNoTerminalControlCharacters } from './cmdShim';

// Re-exported so existing importers keep a single implementation of the guard.
export { assertNoTerminalControlCharacters };

export const enum AnsiColors {
    Green = '\x1b[32m',
    Yellow = '\x1b[33m',
    Blue = '\x1b[34m',
}

export interface AspireTerminal {
    terminal: vscode.Terminal;
    dispose: () => void;
    resolvedCliPath?: string;
}

export interface SendAspireCommandOptions {
    redactAdditionalArgs?: boolean;
    terminalTarget?: 'shared' | 'editor';
    target: CliPathResolutionTarget;
    cliPath?: string;
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

const noExtensionVariablesScrubbedEnvironmentVariables = [
    'ASPIRE_BACKCHANNEL_PATH',
    'ASPIRE_CLI_LOG_FILE',
    'ASPIRE_CLI_PID',
    'ASPIRE_CLI_STARTED',
    'ASPIRE_EXTENSION_CAPABILITIES',
    'ASPIRE_EXTENSION_CERT',
    'ASPIRE_EXTENSION_DEBUG_RUN_MODE',
    'ASPIRE_EXTENSION_DEBUG_SESSION_ID',
    'ASPIRE_EXTENSION_ENDPOINT',
    'ASPIRE_EXTENSION_PROMPT_ENABLED',
    'ASPIRE_EXTENSION_TOKEN',
    'ASPIRE_NON_INTERACTIVE',
    'ASPIRE_SUPPRESS_CLI_RUN_HOOK',
    'DCP_INSTANCE_ID_PREFIX',
    'DEBUG_SESSION_INFO',
    'DEBUG_SESSION_PORT',
    'DEBUG_SESSION_RUN_MODE',
    'DEBUG_SESSION_SERVER_CERTIFICATE',
    'DEBUG_SESSION_TOKEN',
] as const;

const noExtensionVariablesScrubbedEnvironmentVariablePrefixes = [
    'ASPIRE_TERMINAL_HOST_',
] as const;

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
    private _terminalByDebugSessionId = new Map<string, AspireTerminal>();
    private _invalidatedSharedTerminals = new Set<vscode.Terminal>();
    private _rpcServerConnectionInfo?: RpcServerConnectionInfo;
    private _dcpServerConnectionInfo?: DcpServerConnectionInfo;
    private _windowsPowerShellPath?: string;

    private readonly _onDidSendAspireCommand = new vscode.EventEmitter<AspireTerminalCommandEvent>();
    readonly onDidSendAspireCommand = this._onDidSendAspireCommand.event;

    constructor(
        subscriptions: vscode.Disposable[],
        private readonly _isPowerShell7Available = isPowerShell7Available,
        private readonly _cliPathResolver?: CliPathResolver,
    ) {
        subscriptions.push(vscode.window.onDidCloseTerminal(closedTerminal => {
            this._invalidatedSharedTerminals.delete(closedTerminal);
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
        const target = options?.target ?? windowCliPathTarget;
        const cliPath = options?.cliPath ?? await this.getAspireCliExecutablePath(target);
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
                ? this.createAspireEditorTerminal(target, cliPath)
                : this.getAspireTerminal(false, target, cliPath);
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

    getAspireTerminal(
        forceCreate: boolean = false,
        target: CliPathResolutionTarget = windowCliPathTarget,
        resolvedCliPath?: string,
    ): AspireTerminal {
        const terminalKey = this.getTerminalKey(undefined, target);
        let existingTerminal = this._terminalByDebugSessionId.get(terminalKey);
        if (existingTerminal && resolvedCliPath !== undefined && !areResolvedCliPathsEqual(existingTerminal.resolvedCliPath, resolvedCliPath)) {
            this.invalidateSharedAspireTerminal(target);
            existingTerminal = undefined;
        }
        if (existingTerminal) {
            if (!forceCreate) {
                return existingTerminal;
            }
            else {
                existingTerminal.dispose();
            }
        }

        extensionLogOutputChannel.info(`Creating new Aspire terminal`);
        const terminal = this.createTerminal(undefined, target, resolvedCliPath);

        const aspireTerminal: AspireTerminal = {
            terminal,
            resolvedCliPath,
            dispose: () => {
                terminal.dispose();
                this._terminalByDebugSessionId.delete(terminalKey);
            }
        };

        this._terminalByDebugSessionId.set(terminalKey, aspireTerminal);

        return aspireTerminal;
    }

    invalidateSharedAspireTerminal(target?: CliPathResolutionTarget): void {
        const terminalKeys = target
            ? [this.getTerminalKey(undefined, target)]
            : [...this._terminalByDebugSessionId.keys()].filter(key => key.startsWith('shared:'));

        for (const terminalKey of terminalKeys) {
            const existingTerminal = this._terminalByDebugSessionId.get(terminalKey);
            if (!existingTerminal) {
                continue;
            }

            // The terminal may be running a long-lived command, so leave it open. Stop reusing it
            // so the next Aspire command gets a new terminal with the current environment.
            extensionLogOutputChannel.info('Invalidating shared Aspire terminal environment');
            this._terminalByDebugSessionId.delete(terminalKey);
            this._invalidatedSharedTerminals.add(existingTerminal.terminal);
        }
    }

    private createAspireEditorTerminal(target: CliPathResolutionTarget, resolvedCliPath: string): AspireTerminal {
        extensionLogOutputChannel.info('Creating Aspire editor terminal');
        const terminal = this.createTerminal(vscode.TerminalLocation.Editor, target, resolvedCliPath);
        return {
            terminal,
            resolvedCliPath,
            dispose: () => terminal.dispose(),
        };
    }

    private createTerminal(
        location?: vscode.TerminalLocation,
        target: CliPathResolutionTarget = windowCliPathTarget,
        resolvedCliPath?: string,
    ): vscode.Terminal {
        const terminalOptions: vscode.TerminalOptions = {
            name: aspireTerminalName,
            env: this.createEnvironment(undefined, undefined, undefined, resolvedCliPath),
            location,
        };
        if (target.kind === 'workspaceFolder') {
            terminalOptions.cwd = target.workspaceFolder.uri;
        }
        if (process.platform === 'win32') {
            // quoteShellArg uses PowerShell escaping on Windows. Do not rely on the
            // user's default terminal profile because cmd.exe treats backticks as
            // ordinary characters and would make quoted values containing " shell-sensitive again.
            terminalOptions.shellPath = this.getWindowsPowerShellPath();
        }

        return vscode.window.createTerminal(terminalOptions);
    }

    private getTerminalKey(debugSessionId: string | undefined, target: CliPathResolutionTarget): string {
        return `${debugSessionId ?? 'shared'}:${getCliPathTargetKey(target)}`;
    }

    createEnvironment(debugSessionId?: string, noDebug?: boolean, noExtensionVariables?: boolean, resolvedCliPath?: string): any {
        if (noExtensionVariables) {
            const env: any = {
                ...getEnvironmentWithoutE2EBridgeVariables(),

                // Hidden CLI processes still render status/error text that VS Code shows to the user.
                // Keep those messages aligned with the VS Code UI language without enabling the
                // extension RPC/DCP backchannels that noExtensionVariables intentionally suppresses.
                ASPIRE_LOCALE_OVERRIDE: vscode.env.language,
            };

            addForwardableAspireCliPath(env, resolvedCliPath);
            scrubNoExtensionVariablesEnvironment(env);

            return env;
        }

        const env: any = {
            ...getEnvironmentWithoutE2EBridgeVariables(),
        };

        addForwardableAspireCliPath(env, resolvedCliPath);

        Object.assign(env, {
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
        });

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
        env[EnvironmentVariables.ASPIRE_NON_INTERACTIVE] = 'true';

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
        this._invalidatedSharedTerminals.clear();
    }

    dispose() {
        for (const terminal of this._terminalByDebugSessionId.values()) {
            terminal.dispose();
        }
        for (const terminal of this._invalidatedSharedTerminals) {
            terminal.dispose();
        }
        this._invalidatedSharedTerminals.clear();
        this._onDidSendAspireCommand.dispose();
    }


    async resolveAspireCliPath(target: CliPathResolutionTarget = windowCliPathTarget): Promise<CliPathResolutionResult> {
        return this._cliPathResolver
            ? await this._cliPathResolver.resolve(target)
            : await resolveCliPath(target);
    }

    async getAspireCliExecutablePath(target: CliPathResolutionTarget = windowCliPathTarget): Promise<string> {
        const result = await this.resolveAspireCliPath(target);
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

function areResolvedCliPathsEqual(left: string | undefined, right: string): boolean {
    if (left === undefined) {
        return false;
    }

    return process.platform === 'win32'
        ? path.win32.normalize(left).toLowerCase() === path.win32.normalize(right).toLowerCase()
        : left === right;
}

function isPowerShell7Available(): boolean {
    const result = childProcess.spawnSync('pwsh.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], {
        stdio: 'ignore',
        windowsHide: true,
    });

    return result.status === 0 && result.error === undefined;
}

function addForwardableAspireCliPath(env: Record<string, string | undefined>, resolvedCliPath?: string): void {
    // Forward aspire.aspireCliExecutablePath as AspireCliPath so MSBuild's
    // ResolveAspireCliBundle task — which `dotnet build` evaluates whenever
    // the AppHost is built (including from this CLI process and from VS
    // Code's auto-build / language server) — resolves the bundle layout
    // relative to the configured CLI instead of probing PATH. PATH-resolved
    // bundle paths get baked into the AppHost assembly as
    // [AssemblyMetadata("aspireterminalhostpath", …)] and can outlive a
    // dev-loop CLI swap (see https://github.com/microsoft/aspire/issues/18073).
    // Only forward values that pass the task's File.Exists guard; stale
    // absolute paths make the task produce no bundle outputs instead of
    // falling back, and the AppHost targets can then fail with ASPIRE009.
    if (resolvedCliPath !== undefined) {
        // A concrete resolved path (e.g. from spawnCliProcess) is the exact executable this
        // process launches, so it is the only candidate considered; delete any inherited
        // AspireCliPath first so an unforwardable resolvedCliPath can't fall back to stale
        // ambient metadata from a different CLI.
        deleteEnvironmentVariable(env, ASPIRE_CLI_PATH_ENV_VAR);
        const forwardableResolvedPath = getForwardableResolvedAspireCliPath(resolvedCliPath);
        if (forwardableResolvedPath) {
            env[ASPIRE_CLI_PATH_ENV_VAR] = forwardableResolvedPath;
        }

        return;
    }

    const configuredCliPath = getForwardableAspireCliPath();
    if (configuredCliPath) {
        env[ASPIRE_CLI_PATH_ENV_VAR] = configuredCliPath;
    }
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

function scrubNoExtensionVariablesEnvironment(env: Record<string, string | undefined>): void {
    for (const key of noExtensionVariablesScrubbedEnvironmentVariables) {
        deleteEnvironmentVariable(env, key);
    }

    for (const key of Object.keys(env)) {
        if (noExtensionVariablesScrubbedEnvironmentVariablePrefixes.some(prefix => isEnvironmentVariablePrefixMatch(key, prefix))) {
            delete env[key];
        }
    }
}

function deleteEnvironmentVariable(env: Record<string, string | undefined>, name: string): void {
    if (process.platform === 'win32') {
        // Windows environment variable names are case-insensitive; compare uppercased so callers
        // can pass a mixed-case canonical name (e.g. `AspireCliPath`) as well as already-uppercase
        // constants.
        const upperName = name.toUpperCase();
        for (const key of Object.keys(env)) {
            if (key.toUpperCase() === upperName) {
                delete env[key];
            }
        }

        return;
    }

    delete env[name];
}

function isEnvironmentVariablePrefixMatch(key: string, prefix: string): boolean {
    return process.platform === 'win32'
        ? key.toUpperCase().startsWith(prefix)
        : key.startsWith(prefix);
}

function isE2eTerminalCommandExecutionSuppressed(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
        !!process.env.ASPIRE_EXTENSION_E2E_STATE_FILE &&
        !!process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE &&
        process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION === 'true';
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
