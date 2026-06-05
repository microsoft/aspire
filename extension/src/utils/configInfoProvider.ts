import * as vscode from 'vscode';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess } from '../debugger/languages/cli';
import { extensionLogOutputChannel } from './logging';
import { ConfigInfo, FeatureInfo, PropertyInfo, SettingsSchema } from '../types/configInfo';
import * as strings from '../loc/strings';

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
            this._inFlight = undefined;
        }

        if (this._cachedConfigInfo) {
            return this._cachedConfigInfo;
        }

        const inFlight = this._inFlight ??= this._fetchConfigInfo(options?.suppressErrors ?? false);
        try {
            const result = await inFlight;
            if (result && this._inFlight === inFlight) {
                this._cachedConfigInfo = result;
            }
            return result;
        } finally {
            if (this._inFlight === inFlight) {
                this._inFlight = undefined;
            }
        }
    }

    /**
     * Returns whether the CLI advertises the given capability token via `config info`. Capability
     * tokens are stable, locale-independent identifiers (see {@link ConfigInfo.capabilities}).
     */
    async hasCapability(capability: string, options?: { suppressErrors?: boolean; forceRefresh?: boolean }): Promise<boolean> {
        const configInfo = await this.getConfigInfo(options);
        return configInfo?.capabilities?.includes(capability) ?? false;
    }

    private _fetchConfigInfo(suppressErrors: boolean): Promise<ConfigInfo | null> {
        return new Promise<ConfigInfo | null>((resolve) => {
            // Resolve the cli path here (not in the constructor) so a missing CLI is handled by the
            // same error path as a failed spawn rather than throwing during construction.
            this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
                const workingDirectory = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
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
                            const configInfo = parseConfigInfoOutput(output);
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
                    workingDirectory,
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
