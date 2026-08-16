import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess, terminateCliProcess } from './process/cliProcess';
import { extensionLogOutputChannel } from './logging';
import { CapabilityStatus, ConfigInfo, FeatureInfo, PropertyInfo, SettingsSchema } from '../types/configInfo';
import * as strings from '../loc/strings';
import { isNoLogoUnsupportedOutput, noLogoOption, removeRootNoLogoOption } from './cliCompatibility';

const configInfoTimeoutMs = 30_000;
const cliVersionProbeTimeoutMs = 30_000;
const maxCliVersionOutputLength = 128;
const cliVersionPattern = /^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

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

interface ConfigInfoOptions {
    suppressErrors?: boolean;
    forceRefresh?: boolean;
    cliPath?: string;
    cancellationToken?: vscode.CancellationToken;
    minimumVersion?: string;
}

interface CliVersion {
    major: number;
    minor: number;
    patch: number;
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
 *
 * Capability checks can fall back to a minimum CLI version when the token predates structured
 * capability reporting. Version probes are intentionally not cached: an exact launch must observe
 * an executable replaced in place rather than reusing stale support data.
 */
export class ConfigInfoProvider {
    private readonly _cachedConfigInfoByCliPath = new Map<string, ConfigInfo>();
    private readonly _inFlightByCliPath = new Map<string, Promise<ConfigInfo | null>>();
    private readonly _probeGenerationByCliPath = new Map<string, number>();

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    /**
     * Gets configuration information from the Aspire CLI, returning a cached result for the selected
     * CLI executable when available.
     *
     * @param options.suppressErrors When true, failures are logged but not surfaced to the user via
     *   error notifications. Use this for background/best-effort probes (e.g. capability detection)
     *   where a missing or older CLI should degrade silently rather than nag the user.
     * @param options.forceRefresh When true, runs an invocation-owned probe that bypasses cached
     *   and shared in-flight work. A failed refresh leaves the last successful cache entry intact.
     * @param options.cliPath The already-resolved CLI executable path. Supplying this guarantees the
     *   probe describes the same executable the caller is about to invoke.
     * @param options.cancellationToken Cancels this caller's wait. For a force refresh it also
     *   terminates that invocation-owned CLI process; shared probes continue for other callers.
     */
    async getConfigInfo(options?: ConfigInfoOptions): Promise<ConfigInfo | null> {
        const suppressErrors = options?.suppressErrors ?? false;
        if (options?.cancellationToken?.isCancellationRequested) {
            return null;
        }

        const startTime = Date.now();
        const cliPath = options?.cliPath ?? await this._resolveCliPath(suppressErrors, options?.cancellationToken);
        if (!cliPath || options?.cancellationToken?.isCancellationRequested) {
            return null;
        }

        if (!options?.forceRefresh) {
            const cachedConfigInfo = this._cachedConfigInfoByCliPath.get(cliPath);
            if (cachedConfigInfo) {
                return cachedConfigInfo;
            }
        }

        const remainingTimeoutMs = configInfoTimeoutMs - (Date.now() - startTime);
        if (remainingTimeoutMs <= 0) {
            this._reportTimeout(suppressErrors);
            return null;
        }

        if (options?.forceRefresh) {
            const generation = this._beginProbe(cliPath);
            const result = await this._fetchConfigInfo(
                cliPath,
                suppressErrors,
                remainingTimeoutMs,
                options.cancellationToken);
            if (result && this._probeGenerationByCliPath.get(cliPath) === generation) {
                this._cachedConfigInfoByCliPath.set(cliPath, result);
            }
            return result;
        }

        const existingProbe = this._inFlightByCliPath.get(cliPath);
        if (existingProbe) {
            return await this._awaitProbe(
                existingProbe,
                remainingTimeoutMs,
                suppressErrors,
                options?.cancellationToken);
        }

        const probe = this._startSharedProbe(cliPath, suppressErrors, remainingTimeoutMs);
        return await this._awaitProbe(probe, undefined, suppressErrors, options?.cancellationToken);
    }

    /**
     * Returns whether the CLI advertises the given capability token via `config info`. Capability
     * tokens are stable, locale-independent identifiers (see {@link ConfigInfo.capabilities}).
     */
    async hasCapability(capability: string, options?: ConfigInfoOptions): Promise<boolean> {
        return await this.getCapabilityStatus(capability, options) === 'supported';
    }

    /**
     * Distinguishes a successful probe of an older CLI from a probe that could not complete.
     * Callers that must honor an explicit capability-dependent choice cannot safely treat both
     * cases as unsupported.
     */
    async getCapabilityStatus(capability: string, options?: ConfigInfoOptions): Promise<CapabilityStatus> {
        if (!options?.minimumVersion) {
            const configInfo = await this.getConfigInfo(options);
            if (!configInfo) {
                return 'unavailable';
            }

            return configInfo.capabilities?.includes(capability) ? 'supported' : 'unsupported';
        }

        const minimumVersion = parseCliVersion(options.minimumVersion);
        if (!minimumVersion) {
            extensionLogOutputChannel.warn(`Unable to probe Aspire CLI capability '${capability}': invalid minimum version '${options.minimumVersion}'.`);
            return 'unavailable';
        }

        const suppressErrors = options.suppressErrors ?? false;
        const cliPath = options.cliPath ?? await this._resolveCliPath(suppressErrors, options.cancellationToken);
        if (!cliPath || options.cancellationToken?.isCancellationRequested) {
            return 'unavailable';
        }

        const configInfo = await this.getConfigInfo({ ...options, cliPath });
        if (configInfo?.capabilities?.includes(capability)) {
            return 'supported';
        }

        if (options.cancellationToken?.isCancellationRequested) {
            return 'unavailable';
        }

        return await this._getCliMinimumVersionStatus(cliPath, minimumVersion, options.cancellationToken);
    }

    private _getCliMinimumVersionStatus(
        cliPath: string,
        minimumVersion: CliVersion,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<CapabilityStatus> {
        return new Promise<CapabilityStatus>((resolve) => {
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let output = '';
            let outputTooLong = false;
            let settled = false;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            let cancellation: vscode.Disposable | undefined;
            const settle = (result: CapabilityStatus) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                cancellation?.dispose();
                resolve(result);
            };
            const reportUnavailable = (error: unknown) => {
                if (settled) {
                    return;
                }

                extensionLogOutputChannel.warn(`Unable to probe Aspire CLI version: ${String(error)}`);
                settle('unavailable');
            };
            if (cancellationToken?.isCancellationRequested) {
                settle('unavailable');
                return;
            }

            timeout = setTimeout(() => {
                settle('unavailable');
                if (childProcess) {
                    terminateCliProcess(childProcess, 'timed-out Aspire CLI version probe');
                }
            }, cliVersionProbeTimeoutMs);
            cancellation = cancellationToken?.onCancellationRequested(() => {
                settle('unavailable');
                if (childProcess) {
                    terminateCliProcess(childProcess, 'cancelled Aspire CLI version probe');
                }
            });

            try {
                childProcess = spawnCliProcess(this._terminalProvider, cliPath, ['--version'], {
                    createProcessGroup: true,
                    stdoutCallback: value => {
                        if (output.length + value.length > maxCliVersionOutputLength) {
                            outputTooLong = true;
                            return;
                        }

                        output += value;
                    },
                    exitCallback: code => {
                        if (code !== 0 || outputTooLong) {
                            settle('unavailable');
                            return;
                        }

                        const version = parseCliVersion(output);
                        settle(version
                            ? compareCliVersions(version, minimumVersion) >= 0 ? 'supported' : 'unsupported'
                            : 'unavailable');
                    },
                    errorCallback: reportUnavailable,
                    noExtensionVariables: true,
                });
            }
            catch (error) {
                reportUnavailable(error);
            }
        });
    }

    private _startSharedProbe(cliPath: string, suppressErrors: boolean, timeoutMs: number): Promise<ConfigInfo | null> {
        const generation = this._beginProbe(cliPath);
        let probe!: Promise<ConfigInfo | null>;
        probe = this._fetchConfigInfo(cliPath, suppressErrors, timeoutMs)
            .then(result => {
                if (result && this._probeGenerationByCliPath.get(cliPath) === generation) {
                    this._cachedConfigInfoByCliPath.set(cliPath, result);
                }
                return result;
            })
            .finally(() => {
                if (this._inFlightByCliPath.get(cliPath) === probe) {
                    this._inFlightByCliPath.delete(cliPath);
                }
            });
        this._inFlightByCliPath.set(cliPath, probe);
        return probe;
    }

    private _beginProbe(cliPath: string): number {
        const generation = (this._probeGenerationByCliPath.get(cliPath) ?? 0) + 1;
        this._probeGenerationByCliPath.set(cliPath, generation);
        return generation;
    }

    private async _awaitProbe(
        probe: Promise<ConfigInfo | null>,
        timeoutMs: number | undefined,
        suppressErrors: boolean,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<ConfigInfo | null> {
        if (cancellationToken?.isCancellationRequested) {
            return null;
        }

        let timeout: ReturnType<typeof setTimeout> | undefined;
        let cancellation: vscode.Disposable | undefined;
        const callerCompletion = new Promise<null>(resolve => {
            if (timeoutMs !== undefined) {
                timeout = setTimeout(() => {
                    this._reportTimeout(suppressErrors);
                    resolve(null);
                }, timeoutMs);
            }
            cancellation = cancellationToken?.onCancellationRequested(() => resolve(null));
        });

        try {
            // Timeout and cancellation belong to this caller, not the shared process. A caller
            // may leave without terminating work that other subscribers still need.
            return await Promise.race([probe, callerCompletion]);
        }
        finally {
            if (timeout) {
                clearTimeout(timeout);
            }
            cancellation?.dispose();
        }
    }

    private _resolveCliPath(suppressErrors: boolean, cancellationToken?: vscode.CancellationToken): Promise<string | null> {
        return new Promise<string | null>((resolve) => {
            let settled = false;
            let cancellation: vscode.Disposable | undefined;
            const settle = (result: string | null) => {
                if (settled) {
                    return;
                }

                settled = true;
                clearTimeout(timeout);
                cancellation?.dispose();
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
            cancellation = cancellationToken?.onCancellationRequested(() => settle(null));

            try {
                this._terminalProvider.getAspireCliExecutablePath().then(
                    cliPath => settle(cliPath),
                    reportError);
            }
            catch (error) {
                reportError(error);
            }
        });
    }

    private _fetchConfigInfo(
        cliPath: string,
        suppressErrors: boolean,
        timeoutMs: number,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<ConfigInfo | null> {
        return new Promise<ConfigInfo | null>((resolve) => {
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let settled = false;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            let cancellation: vscode.Disposable | undefined;
            const settle = (result: ConfigInfo | null) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                cancellation?.dispose();
                resolve(result);
            };
            const reportError = (error: unknown) => {
                if (settled) {
                    return;
                }

                this._reportError(error, suppressErrors);
                settle(null);
            };
            if (cancellationToken?.isCancellationRequested) {
                settle(null);
                return;
            }

            // The timeout passed here is the remainder of the same 30-second budget that covered
            // executable-path lookup, so a wedged startup probe cannot block callers indefinitely.
            timeout = setTimeout(() => {
                this._reportTimeout(suppressErrors);
                settle(null);

                if (childProcess) {
                    terminateCliProcess(childProcess, 'timed-out aspire config info command');
                }
            }, timeoutMs);
            cancellation = cancellationToken?.onCancellationRequested(() => {
                settle(null);
                if (childProcess) {
                    terminateCliProcess(childProcess, 'cancelled aspire config info command');
                }
            });

            const workingDirectory = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
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

function parseCliVersion(value: string): CliVersion | undefined {
    // `aspire --version` emits a bare semver-like value, for example:
    //   13.2.0
    //   13.2.0-preview.1.12345.6+abcdef
    // Only the bounded numeric core participates in comparison; any other output is unavailable.
    const normalized = value.trim();
    if (normalized.length === 0 || normalized.length > maxCliVersionOutputLength) {
        return undefined;
    }

    const match = cliVersionPattern.exec(normalized);
    if (!match) {
        return undefined;
    }

    return {
        major: Number.parseInt(match[1], 10),
        minor: Number.parseInt(match[2], 10),
        patch: Number.parseInt(match[3], 10),
    };
}

function compareCliVersions(left: CliVersion, right: CliVersion): number {
    return left.major - right.major ||
        left.minor - right.minor ||
        left.patch - right.patch;
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
