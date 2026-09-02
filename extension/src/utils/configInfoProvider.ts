import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { stat } from 'fs/promises';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess, terminateCliProcess } from './process/cliProcess';
import { extensionLogOutputChannel } from './logging';
import { CapabilityStatus, ConfigInfo, FeatureInfo, PropertyInfo, SettingsSchema } from '../types/configInfo';
import * as strings from '../loc/strings';
import { isNoLogoUnsupportedOutput, noLogoOption, removeRootNoLogoOption } from './cliCompatibility';
import { CliPathResolutionTarget, windowCliPathTarget } from './cliPathVariables';
import { nonInteractiveCliEnvironment } from './environment';

const configInfoTimeoutMs = 30_000;
const cliVersionProbeTimeoutMs = 30_000;
// The only available structured update status is part of `aspire doctor`, whose complete
// environment-check budget is two minutes. Keep enough time for that transport; a future narrow
// update-status command should use a substantially smaller bound.
const cliUpdateProbeTimeoutMs = 130_000;
const maxCliVersionOutputLength = 128;
const maxCliUpdateOutputLength = 1024 * 1024;
const cliVersionPattern = /^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

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
    cancellationToken?: vscode.CancellationToken;
    minimumVersion?: string;
    /** The resolution scope to use when `cliPath` is not already known. Defaults to the window scope. */
    target?: CliPathResolutionTarget;
    /** Internal timeout budget for this config-info invocation. */
    timeoutMs?: number;
}

export interface CliVersionStatusOptions {
    cliPath?: string;
    cancellationToken?: vscode.CancellationToken;
    /** The resolution scope to use when `cliPath` is not already known. Defaults to the window scope. */
    target?: CliPathResolutionTarget;
    timeoutMs?: number;
}

export type CliUpdateRecommendationOptions = Omit<CliVersionStatusOptions, 'timeoutMs'> & {
    /** Captured Doctor working directory, when the caller must keep cache and process context aligned. */
    workingDirectory?: string;
};

export interface CliVersionInfo {
    cliPath: string;
    version: string;
    executableIdentity: string;
}

export type CliUpdateRecommendation =
    | { status: 'available'; currentVersion: string; version: string }
    | { status: 'none'; currentVersion: string }
    | { status: 'ineligible'; currentVersion: string }
    | { status: 'unavailable' };

interface CliVersionStatus {
    cliPath: string;
    version: string;
    status: Exclude<CapabilityStatus, 'unavailable'>;
}

interface CliVersion {
    value: string;
    major: number;
    minor: number;
    patch: number;
    isPrerelease: boolean;
    prereleaseIdentifiers: string[];
}

interface CliCaptureResult {
    stdout: string;
    stderr: string;
    exitCode: number | null | undefined;
}

interface CliCaptureOptions {
    timeoutMs: number;
    maxStdoutLength: number;
    maxStderrLength?: number;
    workingDirectory?: string;
    env?: { name: string; value: string }[];
    description: string;
    failureMessage: string;
    cancellationToken?: vscode.CancellationToken;
}

/**
 * Working directory for `aspire config info`, chosen from the resolution target.
 *
 * The CLI discovers `aspire.config.json` by walking up from its working directory, so the folder it
 * runs in decides which local settings file the answer describes. Window-scoped callers have no
 * folder of their own and fall back to the first one, which is the best available guess and matches
 * how other window-scoped commands behave. With no workspace, capture the extension host's current
 * directory rather than leaving process launch to resolve a potentially changed workspace later.
 */
export function resolveConfigInfoWorkingDirectory(target: CliPathResolutionTarget): string {
    if (target.kind === 'workspaceFolder') {
        return target.workspaceFolder.uri.fsPath;
    }

    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
}

/**
 * Wraps `aspire config info --json` and exposes the parsed {@link ConfigInfo} plus capability
 * negotiation helpers. This is the authoritative, locale-independent source for what the installed
 * CLI supports: features and capabilities are reported as structured data rather than parsed from
 * (potentially localized) command output.
 *
 * Successful reads and concurrent probes are cached by CLI executable path and working directory.
 * This lets callers share one invocation without reusing another workspace folder's local settings
 * or capabilities from a different CLI. Failures are intentionally NOT cached: an older CLI that
 * can't answer, or a transient spawn error, should be retried on the next call.
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
     * @param options.target The workspace scope that selects both the CLI and config-info working
     *   directory. Defaults to the window scope.
     */
    async getConfigInfo(options?: ConfigInfoOptions): Promise<ConfigInfo | null> {
        const suppressErrors = options?.suppressErrors ?? false;
        if (options?.cancellationToken?.isCancellationRequested) {
            return null;
        }

        const startTime = Date.now();
        const target = options?.target ?? windowCliPathTarget;
        const cliPath = options?.cliPath ?? await this._resolveCliPath(suppressErrors, target, options?.cancellationToken);
        if (!cliPath || options?.cancellationToken?.isCancellationRequested) {
            return null;
        }

        // `aspire config info` reports the local settings file it discovers from its working
        // directory, so the answer is per-folder, not per-CLI. Keying the caches by CLI path alone
        // let one folder's result be served for another in a multi-root workspace - and callers such
        // as "Open Local Settings" act on `localSettingsPath`, so that opens or creates the wrong file.
        const workingDirectory = resolveConfigInfoWorkingDirectory(target);
        const cacheKey = `${cliPath}\u0000${workingDirectory ?? ''}`;

        if (!options?.forceRefresh) {
            const cachedConfigInfo = this._cachedConfigInfoByCliPath.get(cacheKey);
            if (cachedConfigInfo) {
                return cachedConfigInfo;
            }
        }

        const remainingTimeoutMs = (options?.timeoutMs ?? configInfoTimeoutMs) - (Date.now() - startTime);
        if (remainingTimeoutMs <= 0) {
            this._reportTimeout(suppressErrors);
            return null;
        }

        if (options?.forceRefresh) {
            const generation = this._beginProbe(cacheKey);
            const result = await this._fetchConfigInfo(
                cliPath,
                workingDirectory,
                suppressErrors,
                remainingTimeoutMs,
                options.cancellationToken);
            if (result && this._probeGenerationByCliPath.get(cacheKey) === generation) {
                this._cachedConfigInfoByCliPath.set(cacheKey, result);
            }
            return result;
        }

        const existingProbe = this._inFlightByCliPath.get(cacheKey);
        if (existingProbe) {
            return await this._awaitProbe(
                existingProbe,
                remainingTimeoutMs,
                suppressErrors,
                options?.cancellationToken);
        }

        const probe = this._startSharedProbe(cacheKey, cliPath, workingDirectory, suppressErrors, remainingTimeoutMs);
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
     * Probes the exact resolved Aspire CLI version. The result is intentionally uncached so callers
     * observe an executable replaced in place.
     */
    async getCliVersion(options?: CliVersionStatusOptions): Promise<CliVersionInfo | null> {
        const target = options?.target ?? windowCliPathTarget;
        const startTime = Date.now();
        const timeoutMs = Math.min(options?.timeoutMs ?? cliVersionProbeTimeoutMs, cliVersionProbeTimeoutMs);
        const cliPath = options?.cliPath ?? await this._resolveCliPath(true, target, options?.cancellationToken);
        if (!cliPath || options?.cancellationToken?.isCancellationRequested) {
            return null;
        }

        const remainingTimeoutMs = timeoutMs - (Date.now() - startTime);
        if (remainingTimeoutMs <= 0) {
            return null;
        }

        const version = await this._probeCliVersion(cliPath, remainingTimeoutMs, options?.cancellationToken);
        if (!version) {
            return null;
        }

        const executableIdentity = await getCliExecutableIdentity(
            cliPath,
            timeoutMs - (Date.now() - startTime),
            options?.cancellationToken);
        return executableIdentity
            ? { cliPath, version: version.value, executableIdentity }
            : null;
    }

    /**
     * Returns a same-lane update recommended by the resolved CLI's structured update check. Stable
     * installations follow stable releases and prerelease installations follow prerelease
     * recommendations. Failures, cross-lane recommendations, and no-update results are silent.
     */
    async getCliUpdateRecommendation(options?: CliUpdateRecommendationOptions): Promise<CliUpdateRecommendation> {
        if (options?.cancellationToken?.isCancellationRequested) {
            return { status: 'unavailable' };
        }

        const target = options?.target ?? windowCliPathTarget;
        const cliPath = options?.cliPath ?? await this._resolveCliPath(true, target, options?.cancellationToken);
        if (!cliPath || options?.cancellationToken?.isCancellationRequested) {
            return { status: 'unavailable' };
        }

        const workingDirectory = options?.workingDirectory ?? resolveConfigInfoWorkingDirectory(target);
        return await this._fetchCliUpdateRecommendation(
            cliPath,
            workingDirectory,
            options?.cancellationToken);
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
        const target = options.target ?? windowCliPathTarget;
        const cliPath = options.cliPath ?? await this._resolveCliPath(suppressErrors, target, options.cancellationToken);
        if (!cliPath || options.cancellationToken?.isCancellationRequested) {
            return 'unavailable';
        }

        const probeStartTime = Date.now();
        const probeTimeoutMs = Math.min(options.timeoutMs ?? configInfoTimeoutMs, configInfoTimeoutMs);
        const configInfo = await this.getConfigInfo({ ...options, cliPath, timeoutMs: probeTimeoutMs });
        if (configInfo?.capabilities?.includes(capability)) {
            return 'supported';
        }

        if (options.cancellationToken?.isCancellationRequested) {
            return 'unavailable';
        }

        const remainingTimeoutMs = probeTimeoutMs - (Date.now() - probeStartTime);
        if (remainingTimeoutMs <= 0) {
            return 'unavailable';
        }

        return (await this._getCliVersionStatus(cliPath, minimumVersion, remainingTimeoutMs, options.cancellationToken))?.status
            ?? 'unavailable';
    }

    private async _getCliVersionStatus(
        cliPath: string,
        minimumVersion: CliVersion,
        timeoutMs: number,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<CliVersionStatus | null> {
        const version = await this._probeCliVersion(cliPath, timeoutMs, cancellationToken);
        if (!version) {
            return null;
        }

        return {
            cliPath,
            version: version.value,
            status: compareCliVersions(version, minimumVersion) >= 0 ? 'supported' : 'unsupported',
        };
    }

    private async _probeCliVersion(
        cliPath: string,
        timeoutMs: number,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<CliVersion | null> {
        const result = await this._runCliCapture(cliPath, ['--version'], {
            timeoutMs: Math.min(timeoutMs, cliVersionProbeTimeoutMs),
            maxStdoutLength: maxCliVersionOutputLength,
            description: 'Aspire CLI version probe',
            failureMessage: 'Unable to probe Aspire CLI version',
            cancellationToken,
        });
        return result?.exitCode === 0 ? parseCliVersion(result.stdout) ?? null : null;
    }

    private async _fetchCliUpdateRecommendation(
        cliPath: string,
        workingDirectory: string,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<CliUpdateRecommendation> {
        const deadline = Date.now() + cliUpdateProbeTimeoutMs;
        let args = ['doctor', '--format', 'json', noLogoOption];

        for (let attempt = 0; attempt < 2; attempt++) {
            const result = await this._runCliCapture(cliPath, args, {
                timeoutMs: deadline - Date.now(),
                maxStdoutLength: maxCliUpdateOutputLength,
                maxStderrLength: maxCliUpdateOutputLength,
                workingDirectory,
                env: nonInteractiveCliEnvironment,
                description: 'Aspire CLI update probe',
                failureMessage: 'Unable to check for Aspire CLI updates',
                cancellationToken,
            });
            if (!result) {
                return { status: 'unavailable' };
            }

            try {
                const recommendation = parseCliUpdateRecommendationOutput(result.stdout);
                if (recommendation.status !== 'unavailable' ||
                    isStructuredDoctorOutput(result.stdout) ||
                    !isNoLogoUnsupportedOutput(args, result.stdout, result.stderr)) {
                    return recommendation;
                }
            }
            catch (error) {
                if (!isNoLogoUnsupportedOutput(args, result.stdout, result.stderr)) {
                    extensionLogOutputChannel.warn(`Unable to parse Aspire CLI update status: ${String(error)}`);
                    return { status: 'unavailable' };
                }
            }

            args = removeRootNoLogoOption(args);
        }

        return { status: 'unavailable' };
    }

    private _runCliCapture(
        cliPath: string,
        args: string[],
        options: CliCaptureOptions,
    ): Promise<CliCaptureResult | null> {
        return new Promise<CliCaptureResult | null>((resolve) => {
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let termination: Promise<void> | undefined;
            let stdout = '';
            let stderr = '';
            let stdoutTooLong = false;
            let settled = false;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            let cancellation: vscode.Disposable | undefined;
            const settle = (result: CliCaptureResult | null) => {
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
                if (settled || termination) {
                    return;
                }

                extensionLogOutputChannel.warn(`${options.failureMessage}: ${String(error)}`);
                settle(null);
            };
            const terminateAndSettle = (reason: 'timed-out' | 'cancelled') => {
                if (settled || termination) {
                    return;
                }
                if (!childProcess) {
                    settle(null);
                    return;
                }

                const description = `${reason} ${options.description}`;
                termination = terminateCliProcess(childProcess, description)
                    .catch(error => extensionLogOutputChannel.error(`Failed to terminate ${description}: ${String(error)}`))
                    .then(() => settle(null));
            };
            if (options.timeoutMs <= 0 || options.cancellationToken?.isCancellationRequested) {
                settle(null);
                return;
            }

            timeout = setTimeout(() => {
                terminateAndSettle('timed-out');
            }, options.timeoutMs);
            cancellation = options.cancellationToken?.onCancellationRequested(() => terminateAndSettle('cancelled'));

            try {
                childProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                    createProcessGroup: true,
                    workingDirectory: options.workingDirectory,
                    env: options.env,
                    noExtensionVariables: true,
                    stdoutCallback: value => {
                        if (stdout.length + value.length > options.maxStdoutLength) {
                            stdoutTooLong = true;
                            return;
                        }
                        stdout += value;
                    },
                    stderrCallback: value => {
                        const maxStderrLength = options.maxStderrLength ?? 0;
                        if (stderr.length < maxStderrLength) {
                            stderr += value.slice(0, maxStderrLength - stderr.length);
                        }
                    },
                    exitCallback: code => {
                        if (termination) {
                            return;
                        }
                        if (stdoutTooLong) {
                            settle(null);
                            return;
                        }
                        settle({ stdout, stderr, exitCode: code });
                    },
                    errorCallback: reportUnavailable,
                });
            }
            catch (error) {
                reportUnavailable(error);
            }
        });
    }

    private _startSharedProbe(
        cacheKey: string,
        cliPath: string,
        workingDirectory: string | undefined,
        suppressErrors: boolean,
        timeoutMs: number,
    ): Promise<ConfigInfo | null> {
        const generation = this._beginProbe(cacheKey);
        let probe!: Promise<ConfigInfo | null>;
        probe = this._fetchConfigInfo(cliPath, workingDirectory, suppressErrors, timeoutMs)
            .then(result => {
                if (result && this._probeGenerationByCliPath.get(cacheKey) === generation) {
                    this._cachedConfigInfoByCliPath.set(cacheKey, result);
                }
                return result;
            })
            .finally(() => {
                if (this._inFlightByCliPath.get(cacheKey) === probe) {
                    this._inFlightByCliPath.delete(cacheKey);
                }
            });
        this._inFlightByCliPath.set(cacheKey, probe);
        return probe;
    }

    private _beginProbe(cacheKey: string): number {
        const generation = (this._probeGenerationByCliPath.get(cacheKey) ?? 0) + 1;
        this._probeGenerationByCliPath.set(cacheKey, generation);
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

    private _resolveCliPath(
        suppressErrors: boolean,
        target: CliPathResolutionTarget,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<string | null> {
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
                this._terminalProvider.getAspireCliExecutablePath(target).then(
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
        workingDirectory: string | undefined,
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
                    void terminateCliProcess(childProcess, 'timed-out aspire config info command').catch(error => {
                        extensionLogOutputChannel.error(`Failed to terminate timed-out aspire config info command: ${String(error)}`);
                    });
                }
            }, timeoutMs);
            cancellation = cancellationToken?.onCancellationRequested(() => {
                settle(null);
                if (childProcess) {
                    void terminateCliProcess(childProcess, 'cancelled aspire config info command').catch(error => {
                        extensionLogOutputChannel.error(`Failed to terminate cancelled aspire config info command: ${String(error)}`);
                    });
                }
            });

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

async function getCliExecutableIdentity(
    cliPath: string,
    timeoutMs: number,
    cancellationToken?: vscode.CancellationToken,
): Promise<string | undefined> {
    if (timeoutMs <= 0 || cancellationToken?.isCancellationRequested) {
        return undefined;
    }

    let timeout: ReturnType<typeof setTimeout> | undefined;
    let cancellation: vscode.Disposable | undefined;
    const callerCompletion = new Promise<undefined>(resolve => {
        timeout = setTimeout(() => resolve(undefined), timeoutMs);
        cancellation = cancellationToken?.onCancellationRequested(() => resolve(undefined));
    });
    const fileIdentity = stat(cliPath, { bigint: true })
        // Device/inode detects atomic replacement; size and timestamps also detect in-place rewrites.
        .then(value =>
            `${value.dev}:${value.ino}:${value.mode}:${value.size}:${value.mtimeNs}:${value.ctimeNs}`)
        .catch(() => undefined);

    try {
        return await Promise.race([fileIdentity, callerCompletion]);
    }
    finally {
        if (timeout) {
            clearTimeout(timeout);
        }
        cancellation?.dispose();
    }
}

function parseCliVersion(value: string): CliVersion | undefined {
    // `aspire --version` emits a bare semver-like value, for example:
    //   13.2.0
    //   13.2.0-preview.1.12345.6+abcdef
    // Comparison follows SemVer precedence, including prerelease identifiers, while build metadata
    // remains informational. Any other output is unavailable.
    const normalized = value.trim();
    if (normalized.length === 0 || normalized.length > maxCliVersionOutputLength) {
        return undefined;
    }

    const match = cliVersionPattern.exec(normalized);
    if (!match) {
        return undefined;
    }

    return {
        value: normalized,
        major: Number.parseInt(match[1], 10),
        minor: Number.parseInt(match[2], 10),
        patch: Number.parseInt(match[3], 10),
        isPrerelease: match[4] !== undefined,
        prereleaseIdentifiers: match[4]?.split('.') ?? [],
    };
}

function compareCliVersions(left: CliVersion, right: CliVersion): number {
    const coreComparison = left.major - right.major ||
        left.minor - right.minor ||
        left.patch - right.patch;
    if (coreComparison !== 0) {
        return coreComparison;
    }
    if (left.isPrerelease !== right.isPrerelease) {
        return left.isPrerelease ? -1 : 1;
    }

    for (let index = 0; index < Math.max(left.prereleaseIdentifiers.length, right.prereleaseIdentifiers.length); index++) {
        const leftIdentifier = left.prereleaseIdentifiers[index];
        const rightIdentifier = right.prereleaseIdentifiers[index];
        if (leftIdentifier === undefined || rightIdentifier === undefined) {
            return leftIdentifier === undefined ? -1 : 1;
        }
        if (leftIdentifier === rightIdentifier) {
            continue;
        }

        const leftIsNumeric = /^\d+$/.test(leftIdentifier);
        const rightIsNumeric = /^\d+$/.test(rightIdentifier);
        if (leftIsNumeric !== rightIsNumeric) {
            return leftIsNumeric ? -1 : 1;
        }
        if (leftIsNumeric) {
            const lengthComparison = leftIdentifier.length - rightIdentifier.length;
            if (lengthComparison !== 0) {
                return lengthComparison;
            }
        }

        return leftIdentifier < rightIdentifier ? -1 : 1;
    }

    return 0;
}

export function compareCliVersionValues(left: string, right: string): number | undefined {
    const leftVersion = parseCliVersion(left);
    const rightVersion = parseCliVersion(right);
    return leftVersion && rightVersion ? compareCliVersions(leftVersion, rightVersion) : undefined;
}

export function parseCliUpdateRecommendationOutput(output: string): CliUpdateRecommendation {
    // `aspire doctor --format json` emits:
    //   { "checks": [{ "name": "cli-version", "metadata": {
    //       "currentVersion": "13.5.0", "latestVersion": "13.6.0",
    //       "identityChannel": "stable", "latestVersionChannel": "stable" } }] }
    // Doctor reports the channel baked into the installed assembly. This keeps local/PR/run builds
    // silent even when their process environment emulates a published channel.
    const response = JSON.parse(output.trim()) as { checks?: unknown };
    if (!Array.isArray(response.checks)) {
        return { status: 'unavailable' };
    }

    const check = response.checks.find(value =>
        typeof value === 'object' &&
        value !== null &&
        (value as { name?: unknown }).name === 'cli-version') as { metadata?: unknown } | undefined;
    if (typeof check?.metadata !== 'object' || check.metadata === null) {
        return { status: 'unavailable' };
    }

    const metadata = check.metadata as {
        currentVersion?: unknown;
        latestVersion?: unknown;
        identityChannel?: unknown;
        latestVersionChannel?: unknown;
        updateCheckError?: unknown;
    };
    if (typeof metadata.currentVersion !== 'string') {
        return { status: 'unavailable' };
    }

    const currentVersion = parseCliVersion(metadata.currentVersion);
    if (!currentVersion) {
        return { status: 'unavailable' };
    }

    const identityChannel = normalizePublishedCliChannel(metadata.identityChannel);
    if (!identityChannel) {
        return { status: 'ineligible', currentVersion: currentVersion.value };
    }
    if (typeof metadata.updateCheckError === 'string' && metadata.updateCheckError.length > 0) {
        return { status: 'unavailable' };
    }

    if (metadata.latestVersion === undefined || metadata.latestVersion === null) {
        return { status: 'none', currentVersion: currentVersion.value };
    }
    if (typeof metadata.latestVersion !== 'string') {
        return { status: 'unavailable' };
    }

    const latestVersion = parseCliVersion(metadata.latestVersion);
    if (!latestVersion) {
        return { status: 'unavailable' };
    }

    const latestVersionChannel = metadata.latestVersionChannel === undefined
        ? (latestVersion.isPrerelease ? 'prerelease' : 'stable')
        : normalizeRecommendationChannel(metadata.latestVersionChannel);
    if (!latestVersionChannel) {
        return { status: 'unavailable' };
    }

    const expectedRecommendationChannel = identityChannel === 'stable' ? 'stable' : 'prerelease';
    if (latestVersionChannel !== expectedRecommendationChannel) {
        // Doctor returns its stable recommendation before considering prerelease updates. For an
        // unchanged identity that cross-lane result cannot become actionable, so classify it as
        // ineligible and avoid rerunning the full Doctor battery on the normal refresh interval.
        return { status: 'ineligible', currentVersion: currentVersion.value };
    }

    return { status: 'available', currentVersion: currentVersion.value, version: latestVersion.value };
}

function isStructuredDoctorOutput(output: string): boolean {
    try {
        const response = JSON.parse(output.trim()) as { checks?: unknown };
        return Array.isArray(response.checks);
    }
    catch {
        return false;
    }
}

function normalizePublishedCliChannel(value: unknown): 'stable' | 'daily' | 'staging' | undefined {
    if (typeof value !== 'string') {
        return undefined;
    }

    if (value.length === 0 || value !== value.trim()) {
        return undefined;
    }

    const channel = value.toLowerCase();
    return channel === 'stable' || channel === 'daily' || channel === 'staging'
        ? channel
        : undefined;
}

function normalizeRecommendationChannel(value: unknown): 'stable' | 'prerelease' | undefined {
    if (typeof value !== 'string') {
        return undefined;
    }

    if (value.length === 0 || value !== value.trim()) {
        return undefined;
    }

    const channel = value.toLowerCase();
    return channel === 'stable' || channel === 'prerelease'
        ? channel
        : undefined;
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
