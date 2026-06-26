import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration } from '../dcp/types';
import { startDebuggingDeclined } from '../loc/strings';
import { classifyAppHostDirectory, classifyAppHostPath } from '../utils/appHostLanguage';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { bucketAspireCommand } from '../utils/telemetryBuckets';
import { checkCliAvailableOrRedirect } from '../utils/workspace';

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}

function isAspireCommandType(value: unknown): value is AspireCommandType {
    return value === 'run' || value === 'deploy' || value === 'publish' || value === 'do';
}

function getTerminationCommand(configuration: vscode.DebugConfiguration): AspireCommandType | undefined {
    // Run is the default Aspire command when omitted from launch configuration.
    if (configuration.command === undefined || configuration.command === null) {
        return 'run';
    }

    return isAspireCommandType(configuration.command) ? configuration.command : undefined;
}

export interface AppHostLaunchRequestedEvent {
    appHostPath: string;
    command: AspireCommandType;
    noDebug: boolean;
    doStep?: string;
    executionSuppressed: boolean;
}

export interface AppHostDebugSessionTerminatedEvent {
    appHostPath: string;
    command?: AspireCommandType;
    shouldRequestStopRefresh: boolean;
}

export interface AppHostLaunchOptions {
    cliAvailabilityAlreadyChecked?: boolean;
}

/**
 * Centralizes all Aspire AppHost launch operations that require a resolved
 * AppHost path. Both the editor command provider (which discovers the path)
 * and the tree provider (which extracts it from a tree item) delegate here.
 *
 * Also tracks which AppHost paths are currently in a "launching" state
 * (between the user clicking Run/Debug and the AppHost appearing in the
 * running list or the debug session terminating).
 */
export class AppHostLaunchService implements vscode.Disposable {
    private readonly _launchingPaths = new Set<string>();

    private readonly _onDidChangeLaunchingState = new vscode.EventEmitter<void>();
    readonly onDidChangeLaunchingState = this._onDidChangeLaunchingState.event;

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<AppHostDebugSessionTerminatedEvent>();
    readonly onDidTerminateAppHostDebugSession = this._onDidTerminateAppHostDebugSession.event;

    private readonly _onDidRequestLaunch = new vscode.EventEmitter<AppHostLaunchRequestedEvent>();
    readonly onDidRequestLaunch = this._onDidRequestLaunch.event;

    private readonly _debugSessionSubscription: vscode.Disposable;

    constructor() {
        // When a debug session terminates, clear launching state for that AppHost
        // so the tree reverts from "Starting..." if the launch failed or was cancelled.
        this._debugSessionSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            const appHostPath = session.configuration?.program;
            if (appHostPath && session.configuration?.type === 'aspire') {
                const key = getComparisonKey(path.resolve(appHostPath));
                if (this._launchingPaths.delete(key)) {
                    this._onDidChangeLaunchingState.fire();
                }
                const command = getTerminationCommand(session.configuration);
                this._onDidTerminateAppHostDebugSession.fire({
                    appHostPath,
                    command,
                    shouldRequestStopRefresh: command === 'run',
                });
            }
        });
    }

    dispose(): void {
        this._debugSessionSubscription.dispose();
        this._onDidChangeLaunchingState.dispose();
        this._onDidTerminateAppHostDebugSession.dispose();
        this._onDidRequestLaunch.dispose();
    }

    /**
     * Returns whether the given AppHost path is currently in a launching state.
     */
    get launchingPaths(): readonly string[] {
        return Array.from(this._launchingPaths);
    }

    isLaunching(appHostPath: string): boolean {
        return this._launchingPaths.has(getComparisonKey(path.resolve(appHostPath)));
    }

    /**
     * Clears launching state for the given AppHost path (e.g., when it
     * appears in the running AppHosts list).
     */
    clearLaunching(appHostPath: string): void {
        const key = getComparisonKey(path.resolve(appHostPath));
        if (this._launchingPaths.delete(key)) {
            this._onDidChangeLaunchingState.fire();
        }
    }

    clearMatchingLaunching(appHostPath: string): void {
        const resolvedAppHostPath = path.resolve(appHostPath);
        const exactKey = getComparisonKey(path.normalize(resolvedAppHostPath));
        if (this._launchingPaths.delete(exactKey)) {
            this._onDidChangeLaunchingState.fire();
            return;
        }

        const matchingPaths = Array.from(this._launchingPaths).filter(launchingPath => isMatchingAppHostPath(launchingPath, resolvedAppHostPath));
        if (matchingPaths.length !== 1) {
            return;
        }

        this._launchingPaths.delete(matchingPaths[0]);
        this._onDidChangeLaunchingState.fire();
    }

    /**
     * Launches an Aspire debug session for the given AppHost path.
     * Automatically marks the path as "launching" until it either appears
     * in the running list or the debug session terminates.
     * @param appHostPath Absolute path to the AppHost project.
     * @param command The Aspire CLI command to execute (run, deploy, publish, do).
     * @param noDebug When true, launches without the debugger attached.
     * @param doStep Optional step name for the 'do' command.
     */
    async launch(appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep?: string, options?: AppHostLaunchOptions): Promise<void> {
        const startTime = Date.now();
        const executionSuppressed = isE2eDebugLaunchSuppressed();
        const telemetryProperties = getLaunchTelemetryProperties(appHostPath, command, noDebug, executionSuppressed);

        const config: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${command}: ${vscode.workspace.asRelativePath(appHostPath)}`,
            request: 'launch',
            program: appHostPath,
            command,
            noDebug
        };

        if (doStep) {
            config.step = doStep;
        }

        this._onDidRequestLaunch.fire({
            appHostPath,
            command,
            noDebug,
            doStep,
            executionSuppressed,
        });
        if (executionSuppressed) {
            this.clearLaunching(appHostPath);
            sendTelemetryEvent('apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'suppressed',
            }, {
                duration_ms: Date.now() - startTime,
            });
            return;
        }

        try {
            // Track launching state before awaiting the CLI/debug checks so the tree shows
            // "Starting..." immediately after the user invokes the command. Every pre-start
            // failure path below clears it because VS Code will not emit a terminate event.
            // See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
            this._launchingPaths.add(getComparisonKey(path.resolve(appHostPath)));
            this._onDidChangeLaunchingState.fire();

            if (!options?.cliAvailabilityAlreadyChecked) {
                const cliAvailability = await checkCliAvailableOrRedirect('debug_gate');
                if (!cliAvailability.available) {
                    throw new vscode.CancellationError();
                }
            }
            config.skipCliAvailabilityCheck = true;

            const started = await vscode.debug.startDebugging(undefined, config);
            if (!started) {
                // A false result means VS Code declined the launch before the
                // debug session started (for example, no provider matched or
                // an adapter gate rejected it). Surface it as an error so the
                // tree command path does not silently swallow a real launch
                // failure while still clearing the temporary "Starting..." state.
                const error = new Error(startDebuggingDeclined(command, vscode.workspace.asRelativePath(appHostPath)));
                error.name = 'StartDebuggingDeclined';
                throw error;
            }
            sendTelemetryEvent('apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'success',
            }, {
                duration_ms: Date.now() - startTime,
            });
        } catch (err) {
            this.clearLaunching(appHostPath);
            const canceled = isCommandCancellation(err);
            const properties: EventProperties<'apphost/launch/result'> = {
                ...telemetryProperties,
                outcome: canceled ? 'canceled' : 'error',
            };
            if (!canceled) {
                properties.error_kind = classifyError(err);
            }
            sendTelemetryEvent('apphost/launch/result', properties, {
                duration_ms: Date.now() - startTime,
            });
            throw err;
        }
    }
}

function getLaunchTelemetryProperties(appHostPath: string, command: AspireCommandType, noDebug: boolean, executionSuppressed: boolean) {
    const isDirectory = isDirectoryForTelemetry(appHostPath);
    return {
        mode: noDebug ? 'run' : 'debug',
        command: bucketAspireCommand(command),
        apphost_language: isDirectory ? classifyAppHostDirectory(appHostPath) : classifyAppHostPath(appHostPath),
        execution_suppressed: executionSuppressed ? 'true' : 'false',
    };
}

function isDirectoryForTelemetry(appHostPath: string): boolean {
    try {
        return fs.statSync(appHostPath, { throwIfNoEntry: false })?.isDirectory() === true;
    }
    catch {
        return false;
    }
}

function isE2eDebugLaunchSuppressed(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
        !!process.env.ASPIRE_EXTENSION_E2E_STATE_FILE &&
        !!process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE &&
        process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH === 'true';
}

function isMatchingAppHostPath(left: string, right: string): boolean {
    const normalizedLeft = path.normalize(left);
    const normalizedRight = path.normalize(right);
    if (getComparisonKey(normalizedLeft) === getComparisonKey(normalizedRight)) {
        return true;
    }

    return getComparisonKey(path.dirname(normalizedLeft)) === getComparisonKey(path.dirname(normalizedRight)) &&
        isProjectFileToSourceFileMatch(normalizedLeft, normalizedRight);
}

function isProjectFileToSourceFileMatch(left: string, right: string): boolean {
    return (isProjectFile(left) && isSourceFile(right)) || (isSourceFile(left) && isProjectFile(right));
}

function isProjectFile(value: string): boolean {
    return path.extname(value).toLowerCase() === '.csproj';
}

function isSourceFile(value: string): boolean {
    const fileName = path.basename(value).toLowerCase();
    return fileName === 'apphost.cs' || fileName === 'program.cs';
}
