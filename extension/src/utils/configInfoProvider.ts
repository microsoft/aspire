import * as vscode from 'vscode';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess } from '../debugger/languages/cli';
import { extensionLogOutputChannel } from './logging';
import { ConfigInfo } from '../types/configInfo';
import * as strings from '../loc/strings';

/**
 * Wraps `aspire config info --json` and exposes the parsed {@link ConfigInfo} plus capability
 * negotiation helpers. This is the authoritative, locale-independent source for what the installed
 * CLI supports: features and capabilities are reported as structured data rather than parsed from
 * (potentially localized) command output.
 *
 * The provider caches the first successful read for its lifetime and de-duplicates concurrent
 * probes so callers (e.g. the resource view warming capabilities on startup while a command handler
 * reads settings paths) share a single CLI invocation. Failures are intentionally NOT cached: an
 * older CLI that can't answer, or a transient spawn error, should be retried on the next call.
 */
export class ConfigInfoProvider {
    private _cachedConfigInfo: ConfigInfo | undefined;
    private _inFlight: Promise<ConfigInfo | null> | undefined;

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    /**
     * Gets configuration information from the Aspire CLI, returning a cached result when available.
     *
     * @param options.suppressErrors When true, failures are logged but not surfaced to the user via
     *   error notifications. Use this for background/best-effort probes (e.g. capability detection)
     *   where a missing or older CLI should degrade silently rather than nag the user.
     * @param options.forceRefresh When true, bypasses and clears the cache so the CLI is queried again.
     */
    async getConfigInfo(options?: { suppressErrors?: boolean; forceRefresh?: boolean }): Promise<ConfigInfo | null> {
        if (options?.forceRefresh) {
            this._cachedConfigInfo = undefined;
        }

        if (this._cachedConfigInfo) {
            return this._cachedConfigInfo;
        }

        this._inFlight ??= this._fetchConfigInfo(options?.suppressErrors ?? false);
        try {
            const result = await this._inFlight;
            if (result) {
                this._cachedConfigInfo = result;
            }
            return result;
        } finally {
            this._inFlight = undefined;
        }
    }

    /**
     * Returns whether the CLI advertises the given capability token via `config info`. Capability
     * tokens are stable, locale-independent identifiers (see {@link ConfigInfo.capabilities}).
     */
    async hasCapability(capability: string, options?: { suppressErrors?: boolean }): Promise<boolean> {
        const configInfo = await this.getConfigInfo(options);
        return configInfo?.capabilities?.includes(capability) ?? false;
    }

    private _fetchConfigInfo(suppressErrors: boolean): Promise<ConfigInfo | null> {
        return new Promise<ConfigInfo | null>((resolve) => {
            // Resolve the cli path here (not in the constructor) so a missing CLI is handled by the
            // same error path as a failed spawn rather than throwing during construction.
            this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
                const args = ['config', 'info', '--json'];
                let output = '';

                spawnCliProcess(this._terminalProvider, cliPath, args, {
                    stdoutCallback: (data) => {
                        output += data;
                    },
                    stderrCallback: (data) => {
                        extensionLogOutputChannel.error(`aspire config info stderr: ${data}`);
                    },
                    exitCallback: (code) => {
                        if (code !== 0) {
                            extensionLogOutputChannel.error(strings.failedToGetConfigInfo(code ?? -1));
                            if (!suppressErrors) {
                                vscode.window.showErrorMessage(strings.failedToGetConfigInfo(code ?? -1));
                            }
                            resolve(null);
                            return;
                        }

                        try {
                            const configInfo = JSON.parse(output.trim()) as ConfigInfo;
                            extensionLogOutputChannel.info(`Got config info: ${configInfo.availableFeatures.length} features available`);
                            resolve(configInfo);
                        } catch (error) {
                            extensionLogOutputChannel.error(strings.failedToParseConfigInfo(error));
                            if (!suppressErrors) {
                                vscode.window.showErrorMessage(strings.failedToParseConfigInfo(error));
                            }
                            resolve(null);
                        }
                    },
                    errorCallback: (error) => {
                        extensionLogOutputChannel.error(strings.errorGettingConfigInfo(error));
                        if (!suppressErrors) {
                            vscode.window.showErrorMessage(strings.errorGettingConfigInfo(error));
                        }
                        resolve(null);
                    },
                    noExtensionVariables: true
                });
            }, error => {
                extensionLogOutputChannel.error(strings.errorGettingConfigInfo(error));
                if (!suppressErrors) {
                    vscode.window.showErrorMessage(strings.errorGettingConfigInfo(error));
                }
                resolve(null);
            });
        });
    }
}
