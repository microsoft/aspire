import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess, terminateCliProcess } from './process/cliProcess';
import { extensionLogOutputChannel } from './logging';
import { ConfigInfo, FeatureInfo, PropertyInfo, SettingsSchema } from '../types/configInfo';
import * as strings from '../loc/strings';
import { isNoLogoUnsupportedOutput, noLogoOption, removeRootNoLogoOption } from './cliCompatibility';
import { CliPathResolutionTarget, windowCliPathTarget } from './cliPathVariables';

const configInfoTimeoutMs = 30_000;

type RawFeatureInfo = Partial<FeatureInfo> & {
    Name?: unknown;
    Description?: unknown;
    DefaultValue?: unknown;
};

type RawPropertyInfo = Partial<PropertyInfo> & {
    Name?: unknown;
    Type?: unknown;
    Description?: unknown;
    Required?: unknown;
    SubProperties?: unknown;
    AdditionalPropertiesType?: unknown;
};

type RawSettingsSchema = Partial<SettingsSchema> & {
    Properties?: unknown;
};

type RawConfigInfo = Partial<ConfigInfo> & {
    LocalSettingsPath?: unknown;
    GlobalSettingsPath?: unknown;
    AvailableFeatures?: unknown;
    LocalSettingsSchema?: unknown;
    GlobalSettingsSchema?: unknown;
    ConfigFileSchema?: unknown;
    Capabilities?: unknown;
};

export async function getConfigInfo(terminalProvider: AspireTerminalProvider): Promise<ConfigInfo | null> {
    return new ConfigInfoProvider(terminalProvider).getConfigInfo();
}

export interface ConfigInfoOptions {
    suppressErrors?: boolean;
    forceRefresh?: boolean;
    cliPath?: string;
    /** The resolution scope to use when `cliPath` is not already known. Defaults to the window scope. */
    target?: CliPathResolutionTarget;
}

/**
 * Wraps `aspire config info --json` and exposes the parsed {@link ConfigInfo} plus capability
 * negotiation helpers. This is the authoritative, locale-independent source for what the installed
 * CLI supports: features and capabilities are reported as structured data rather than parsed from
 * (potentially localized) command output.
 *
 * Successful reads and concurrent probes are cached by CLI executable path. This lets callers share
 * one invocation while ensuring that changing `aspire.aspireCliExecutablePath` cannot reuse
 * capabilities from a different CLI. Failures are intentionally NOT cached: an older CLI that can't
 * answer, or a transient spawn error, should be retried on the next call.
 */
/**
 * Working directory for `aspire config info`, chosen from the resolution target.
 *
 * The CLI discovers `aspire.config.json` by walking up from its working directory, so the folder it
 * runs in decides which local settings file the answer describes. Window-scoped callers have no
 * folder of their own and fall back to the first one, which is the best available guess and matches
 * how other window-scoped commands behave.
 */
function resolveConfigInfoWorkingDirectory(target: CliPathResolutionTarget): string | undefined {
    if (target.kind === 'workspaceFolder') {
        return target.workspaceFolder.uri.fsPath;
    }

    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
}

export class ConfigInfoProvider {
    private readonly _cachedConfigInfoByCliPath = new Map<string, ConfigInfo>();
    private readonly _inFlightByCliPath = new Map<string, Promise<ConfigInfo | null>>();

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    /**
     * Gets configuration information from the Aspire CLI, returning a cached result for the selected
     * CLI executable when available.
     *
     * @param options.suppressErrors When true, failures are logged but not surfaced to the user via
     *   error notifications. Use this for background/best-effort probes (e.g. capability detection)
     *   where a missing or older CLI should degrade silently rather than nag the user.
     * @param options.forceRefresh When true, bypasses cached and in-flight results for the selected
     *   CLI path so the executable is queried again.
     * @param options.cliPath The already-resolved CLI executable path. Supplying this guarantees the
     *   probe describes the same executable the caller is about to invoke.
     */
    async getConfigInfo(options?: ConfigInfoOptions): Promise<ConfigInfo | null> {
        const suppressErrors = options?.suppressErrors ?? false;
        const startTime = Date.now();
        const target = options?.target ?? windowCliPathTarget;
        const cliPath = options?.cliPath ?? await this._resolveCliPath(suppressErrors, target);
        if (!cliPath) {
            return null;
        }

        // `aspire config info` reports the local settings file it discovers from its working
        // directory, so the answer is per-folder, not per-CLI. Keying the caches by CLI path alone
        // let one folder's result be served for another in a multi-root workspace - and callers such
        // as "Open Local Settings" act on `localSettingsPath`, so that opens or creates the wrong file.
        const workingDirectory = resolveConfigInfoWorkingDirectory(target);
        const cacheKey = `${cliPath}\u0000${workingDirectory ?? ''}`;

        if (options?.forceRefresh) {
            this._cachedConfigInfoByCliPath.delete(cacheKey);
        }
        else {
            const cachedConfigInfo = this._cachedConfigInfoByCliPath.get(cacheKey);
            if (cachedConfigInfo) {
                return cachedConfigInfo;
            }
        }

        const remainingTimeoutMs = configInfoTimeoutMs - (Date.now() - startTime);
        if (remainingTimeoutMs <= 0) {
            this._reportTimeout(suppressErrors);
            return null;
        }

        if (!options?.forceRefresh) {
            const existingProbe = this._inFlightByCliPath.get(cacheKey);
            if (existingProbe) {
                return await this._awaitProbe(existingProbe, remainingTimeoutMs, suppressErrors);
            }
        }

        const probe = this._fetchConfigInfo(cliPath, workingDirectory, suppressErrors, remainingTimeoutMs);
        this._inFlightByCliPath.set(cacheKey, probe);
        try {
            const result = await probe;
            if (result && this._inFlightByCliPath.get(cacheKey) === probe) {
                this._cachedConfigInfoByCliPath.set(cacheKey, result);
            }
            return result;
        }
        finally {
            if (this._inFlightByCliPath.get(cacheKey) === probe) {
                this._inFlightByCliPath.delete(cacheKey);
            }
        }
    }

    /**
     * Returns whether the CLI advertises the given capability token via `config info`. Capability
     * tokens are stable, locale-independent identifiers (see {@link ConfigInfo.capabilities}).
     */
    async hasCapability(capability: string, options?: ConfigInfoOptions): Promise<boolean> {
        const configInfo = await this.getConfigInfo(options);
        return configInfo?.capabilities?.includes(capability) ?? false;
    }

    private async _awaitProbe(probe: Promise<ConfigInfo | null>, timeoutMs: number, suppressErrors: boolean): Promise<ConfigInfo | null> {
        let timeout: ReturnType<typeof setTimeout> | undefined;
        const timeoutPromise = new Promise<null>(resolve => {
            timeout = setTimeout(() => {
                this._reportTimeout(suppressErrors);
                resolve(null);
            }, timeoutMs);
        });

        try {
            // This timeout belongs to the caller, not the shared process. A caller that spent most of
            // its budget resolving the CLI path must be allowed to leave without cancelling the probe
            // for subscribers that joined later with a fresh budget.
            return await Promise.race([probe, timeoutPromise]);
        }
        finally {
            if (timeout) {
                clearTimeout(timeout);
            }
        }
    }

    private _resolveCliPath(suppressErrors: boolean, target: CliPathResolutionTarget): Promise<string | null> {
        return new Promise<string | null>((resolve) => {
            let settled = false;
            const settle = (result: string | null) => {
                if (settled) {
                    return;
                }

                settled = true;
                clearTimeout(timeout);
                resolve(result);
            };
            const reportError = (error: unknown) => {
                if (settled) {
                    return;
                }

                this._reportError(error, suppressErrors);
                settle(null);
            };
            const timeout = setTimeout(() => {
                this._reportTimeout(suppressErrors);
                settle(null);
            }, configInfoTimeoutMs);

            try {
                this._terminalProvider.getAspireCliExecutablePath(target).then(
                    cliPath => settle(cliPath),
                    reportError);
            }
            catch (error) {
                reportError(error);
            }
        });
    }

    private _fetchConfigInfo(cliPath: string, workingDirectory: string | undefined, suppressErrors: boolean, timeoutMs: number): Promise<ConfigInfo | null> {
        return new Promise<ConfigInfo | null>((resolve) => {
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let settled = false;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            const settle = (result: ConfigInfo | null) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                resolve(result);
            };
            const reportError = (error: unknown) => {
                if (settled) {
                    return;
                }

                this._reportError(error, suppressErrors);
                settle(null);
            };

            // The timeout passed here is the remainder of the same 30-second budget that covered
            // executable-path lookup, so a wedged startup probe cannot block callers indefinitely.
            timeout = setTimeout(() => {
                this._reportTimeout(suppressErrors);
                settle(null);

                if (childProcess) {
                    terminateCliProcess(childProcess, 'timed-out aspire config info command');
                }
            }, timeoutMs);

            const runConfigInfo = (args: string[], allowNoLogoRetry: boolean) => {
                if (settled) {
                    return;
                }

                let output = '';
                let stderr = '';

                try {
                    childProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                        createProcessGroup: true,
                        stdoutCallback: (data) => {
                            output += data;
                        },
                        stderrCallback: (data) => {
                            stderr += data;
                        },
                        exitCallback: (code) => {
                            if (settled) {
                                return;
                            }

                            if (code !== 0) {
                                if (allowNoLogoRetry && isNoLogoUnsupportedOutput(args, output, stderr)) {
                                    extensionLogOutputChannel.info(`Installed Aspire CLI does not recognize ${noLogoOption}; retrying config info without it.`);
                                    runConfigInfo(removeRootNoLogoOption(args), false);
                                    return;
                                }

                                if (stderr) {
                                    extensionLogOutputChannel.error(`aspire config info stderr: ${stderr}`);
                                }
                                extensionLogOutputChannel.error(strings.failedToGetConfigInfo(code ?? -1));
                                if (!suppressErrors) {
                                    vscode.window.showErrorMessage(strings.failedToGetConfigInfo(code ?? -1));
                                }
                                settle(null);
                                return;
                            }

                            try {
                                const configInfo = parseConfigInfoOutput(output);
                                extensionLogOutputChannel.info(`Got config info: ${configInfo.availableFeatures.length} features available`);
                                settle(configInfo);
                            }
                            catch (error) {
                                if (stderr) {
                                    extensionLogOutputChannel.error(`aspire config info stderr: ${stderr}`);
                                }
                                extensionLogOutputChannel.error(strings.failedToParseConfigInfo(error));
                                if (!suppressErrors) {
                                    vscode.window.showErrorMessage(strings.failedToParseConfigInfo(error));
                                }
                                settle(null);
                            }
                        },
                        errorCallback: reportError,
                        workingDirectory,
                        noExtensionVariables: true
                    });
                }
                catch (error) {
                    reportError(error);
                }
            };

            runConfigInfo(['config', 'info', '--json', noLogoOption], true);
        });
    }

    private _reportError(error: unknown, suppressErrors: boolean): void {
        extensionLogOutputChannel.error(strings.errorGettingConfigInfo(error));
        if (!suppressErrors) {
            vscode.window.showErrorMessage(strings.errorGettingConfigInfo(error));
        }
    }

    private _reportTimeout(suppressErrors: boolean): void {
        const message = strings.configInfoTimedOut(configInfoTimeoutMs / 1000);
        extensionLogOutputChannel.warn(message);
        if (!suppressErrors) {
            vscode.window.showErrorMessage(message);
        }
    }
}

export function parseConfigInfoOutput(output: string): ConfigInfo {
    const configInfo = JSON.parse(output.trim()) as RawConfigInfo;

    return {
        localSettingsPath: readString(configInfo.localSettingsPath ?? configInfo.LocalSettingsPath, 'localSettingsPath'),
        globalSettingsPath: readString(configInfo.globalSettingsPath ?? configInfo.GlobalSettingsPath, 'globalSettingsPath'),
        availableFeatures: readArray(configInfo.availableFeatures ?? configInfo.AvailableFeatures, 'availableFeatures').map(normalizeFeatureInfo),
        localSettingsSchema: normalizeSettingsSchema(configInfo.localSettingsSchema ?? configInfo.LocalSettingsSchema, 'localSettingsSchema'),
        globalSettingsSchema: normalizeSettingsSchema(configInfo.globalSettingsSchema ?? configInfo.GlobalSettingsSchema, 'globalSettingsSchema'),
        configFileSchema: normalizeOptionalSettingsSchema(configInfo.configFileSchema ?? configInfo.ConfigFileSchema, 'configFileSchema'),
        capabilities: normalizeOptionalStringArray(configInfo.capabilities ?? configInfo.Capabilities, 'capabilities'),
    };
}

function normalizeFeatureInfo(value: unknown): FeatureInfo {
    const feature = readObject<RawFeatureInfo>(value, 'availableFeatures[]');

    return {
        name: readString(feature.name ?? feature.Name, 'availableFeatures[].name'),
        description: readString(feature.description ?? feature.Description, 'availableFeatures[].description'),
        defaultValue: readBoolean(feature.defaultValue ?? feature.DefaultValue, 'availableFeatures[].defaultValue'),
    };
}

function normalizeSettingsSchema(value: unknown, propertyName: string): SettingsSchema {
    const schema = readObject<RawSettingsSchema>(value, propertyName);

    return {
        properties: readArray(schema.properties ?? schema.Properties, `${propertyName}.properties`).map(normalizePropertyInfo),
    };
}

function normalizeOptionalSettingsSchema(value: unknown, propertyName: string): SettingsSchema | undefined {
    if (value === undefined) {
        return undefined;
    }

    return normalizeSettingsSchema(value, propertyName);
}

function normalizePropertyInfo(value: unknown): PropertyInfo {
    const property = readObject<RawPropertyInfo>(value, 'properties[]');
    const subProperties = property.subProperties ?? property.SubProperties;
    const additionalPropertiesType = property.additionalPropertiesType ?? property.AdditionalPropertiesType;

    return {
        name: readString(property.name ?? property.Name, 'properties[].name'),
        type: readString(property.type ?? property.Type, 'properties[].type'),
        description: readString(property.description ?? property.Description, 'properties[].description'),
        required: readBoolean(property.required ?? property.Required, 'properties[].required'),
        subProperties: subProperties === undefined ? undefined : readArray(subProperties, 'properties[].subProperties').map(normalizePropertyInfo),
        additionalPropertiesType: additionalPropertiesType === undefined ? undefined : readString(additionalPropertiesType, 'properties[].additionalPropertiesType'),
    };
}

function readObject<T extends object>(value: unknown, propertyName: string): T {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        throw new Error(`Expected ${propertyName} to be an object.`);
    }

    return value as T;
}

function readArray(value: unknown, propertyName: string): unknown[] {
    if (!Array.isArray(value)) {
        throw new Error(`Expected ${propertyName} to be an array.`);
    }

    return value;
}

function readString(value: unknown, propertyName: string): string {
    if (typeof value !== 'string') {
        throw new Error(`Expected ${propertyName} to be a string.`);
    }

    return value;
}

function readBoolean(value: unknown, propertyName: string): boolean {
    if (typeof value !== 'boolean') {
        throw new Error(`Expected ${propertyName} to be a boolean.`);
    }

    return value;
}

function normalizeOptionalStringArray(value: unknown, propertyName: string): string[] | undefined {
    if (value === undefined) {
        return undefined;
    }

    return readArray(value, propertyName).map((item, index) => readString(item, `${propertyName}[${index}]`));
}
