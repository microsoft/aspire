import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration } from '../dcp/types';
import { appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { startDebuggingDeclined } from '../loc/strings';
import { classifyAppHostDirectory, classifyAppHostPath } from '../utils/appHostLanguage';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { bucketAspireCommand } from '../utils/telemetryBuckets';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { isSameFileSystemEntry } from '../utils/appHostDiscovery';

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

function getDebugConfigurationAppHostPath(configuration: vscode.DebugConfiguration): string | undefined {
    const telemetryTargetPath = configuration[appHostTelemetryTargetPathConfigKey];
    if (typeof telemetryTargetPath === 'string') {
        return telemetryTargetPath;
    }

    return typeof configuration.program === 'string' ? configuration.program : undefined;
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
    shouldMarkAppHostStopping: boolean;
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
    // Session termination can arrive after running-host reconciliation cleared and recreated path
    // state. Persist a per-launch token in the debug configuration so stale sessions only release
    // the ownership they created, rather than a newer launch of the same AppHost.
    private readonly _launchingPathOwners = new Map<string, Set<number>>();
    private readonly _launchingPathByToken = new Map<number, string>();
    private readonly _activeRunDebugSessionPaths = new Map<string, string>();
    private readonly _pendingRunPathByToken = new Map<number, string>();
    private _nextLaunchToken = 0;

    private readonly _onDidChangeLaunchingState = new vscode.EventEmitter<void>();
    readonly onDidChangeLaunchingState = this._onDidChangeLaunchingState.event;

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<AppHostDebugSessionTerminatedEvent>();
    readonly onDidTerminateAppHostDebugSession = this._onDidTerminateAppHostDebugSession.event;

    private readonly _onDidRequestLaunch = new vscode.EventEmitter<AppHostLaunchRequestedEvent>();
    readonly onDidRequestLaunch = this._onDidRequestLaunch.event;

    private readonly _debugSessionSubscription: vscode.Disposable;

    constructor() {
        const startSubscription = vscode.debug.onDidStartDebugSession(session => {
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
            }

            const appHostPath = getDebugConfigurationAppHostPath(session.configuration);
            if (appHostPath &&
                session.configuration?.type === 'aspire' &&
                getTerminationCommand(session.configuration) === 'run') {
                this._activeRunDebugSessionPaths.set(session.id, appHostPath);
            }
        });

        // When a debug session terminates, clear launching state for that AppHost
        // so the tree reverts from "Starting..." if the launch failed or was cancelled.
        const terminateSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            this._activeRunDebugSessionPaths.delete(session.id);
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
            }

            const appHostPath = getDebugConfigurationAppHostPath(session.configuration);
            if (appHostPath && session.configuration?.type === 'aspire') {
                if (typeof launchToken === 'number' && this.releaseLaunchingToken(launchToken)) {
                    this._onDidChangeLaunchingState.fire();
                }
                const command = getTerminationCommand(session.configuration);
                const shouldRequestStopRefresh = command === 'run';
                const restartSourceSessionId = session.configuration[appHostRestartSourceSessionIdConfigKey];
                const isToolbarRestart = typeof restartSourceSessionId === 'string' && restartSourceSessionId === session.id;
                this._onDidTerminateAppHostDebugSession.fire({
                    appHostPath,
                    command,
                    shouldRequestStopRefresh,
                    shouldMarkAppHostStopping: shouldRequestStopRefresh &&
                        !isToolbarRestart &&
                        !this.hasPendingOrActiveRunDebugSession(getDebugConfigurationAppHostPath(session.configuration) ?? appHostPath),
                });
            }
        });
        this._debugSessionSubscription = vscode.Disposable.from(startSubscription, terminateSubscription);
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
        return Array.from(this._launchingPathOwners.keys());
    }

    isLaunching(appHostPath: string): boolean {
        return this.findLaunchingPath(appHostPath) !== undefined;
    }

    /**
     * Clears launching state for the given AppHost path (e.g., when it
     * appears in the running AppHosts list).
     */
    clearLaunching(appHostPath: string): void {
        if (this.deleteLaunchingPath(appHostPath)) {
            this._onDidChangeLaunchingState.fire();
        }
    }

    clearMatchingLaunching(appHostPath: string): void {
        const resolvedAppHostPath = path.resolve(appHostPath);
        if (this.deleteLaunchingPath(resolvedAppHostPath)) {
            this._onDidChangeLaunchingState.fire();
            return;
        }

        const matchingPaths = Array.from(this._launchingPathOwners.keys()).filter(
            launchingPath => isMatchingAppHostPath(launchingPath, resolvedAppHostPath));
        if (matchingPaths.length !== 1) {
            return;
        }

        this.deleteLaunchingPath(matchingPaths[0]);
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
    async launch(appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep?: string): Promise<void> {
        const startTime = Date.now();
        const launchToken = ++this._nextLaunchToken;
        const executionSuppressed = isE2eDebugLaunchSuppressed();
        if (!executionSuppressed && command === 'run') {
            this._pendingRunPathByToken.set(launchToken, appHostPath);
        }

        let telemetryProperties: Awaited<ReturnType<typeof getLaunchTelemetryProperties>>;
        try {
            telemetryProperties = await getLaunchTelemetryProperties(appHostPath, command, noDebug, executionSuppressed);
        }
        catch (err) {
            this._pendingRunPathByToken.delete(launchToken);
            throw err;
        }

        const config: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${command}: ${vscode.workspace.asRelativePath(appHostPath)}`,
            request: 'launch',
            program: appHostPath,
            command,
            noDebug,
            [appHostSelectionOriginConfigKey]: 'user-selection',
            [appHostLaunchTokenConfigKey]: launchToken,
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
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', {
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
            if (this.addLaunchingPath(appHostPath, launchToken)) {
                this._onDidChangeLaunchingState.fire();
            }

            const cliAvailability = await checkCliAvailableOrRedirect('debug_gate');
            if (!cliAvailability.available) {
                throw new vscode.CancellationError();
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
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'success',
            }, {
                duration_ms: Date.now() - startTime,
            });
        } catch (err) {
            this._pendingRunPathByToken.delete(launchToken);
            if (this.releaseLaunchingToken(launchToken)) {
                this._onDidChangeLaunchingState.fire();
            }
            const canceled = isCommandCancellation(err);
            const properties: EventProperties<'aspire/vscode/apphost/launch/result'> = {
                ...telemetryProperties,
                outcome: canceled ? 'canceled' : 'error',
            };
            if (!canceled) {
                properties.error_kind = classifyError(err);
            }
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', properties, {
                duration_ms: Date.now() - startTime,
            });
            throw err;
        }
    }

    private findLaunchingPath(appHostPath: string): string | undefined {
        return Array.from(this._launchingPathOwners.keys())
            .find(launchingPath => isSameFileSystemEntry(launchingPath, appHostPath));
    }

    private addLaunchingPath(appHostPath: string, launchToken: number): boolean {
        const launchingPath = this.findLaunchingPath(appHostPath) ?? path.resolve(appHostPath);
        let owners = this._launchingPathOwners.get(launchingPath);
        const launchingStateChanged = owners === undefined;
        if (!owners) {
            owners = new Set<number>();
            this._launchingPathOwners.set(launchingPath, owners);
        }

        owners.add(launchToken);
        this._launchingPathByToken.set(launchToken, launchingPath);
        return launchingStateChanged;
    }

    private deleteLaunchingPath(appHostPath: string): boolean {
        const launchingPath = this.findLaunchingPath(appHostPath);
        if (launchingPath === undefined) {
            return false;
        }

        const owners = this._launchingPathOwners.get(launchingPath);
        if (owners) {
            for (const launchToken of owners) {
                this._launchingPathByToken.delete(launchToken);
            }
        }

        return this._launchingPathOwners.delete(launchingPath);
    }

    private releaseLaunchingToken(launchToken: number): boolean {
        const launchingPath = this._launchingPathByToken.get(launchToken);
        if (launchingPath === undefined) {
            return false;
        }

        this._launchingPathByToken.delete(launchToken);
        const owners = this._launchingPathOwners.get(launchingPath);
        if (!owners) {
            return false;
        }

        owners.delete(launchToken);
        if (owners.size > 0) {
            return false;
        }

        return this._launchingPathOwners.delete(launchingPath);
    }

    private hasPendingOrActiveRunDebugSession(appHostPath: string): boolean {
        return [...this._pendingRunPathByToken.values(), ...this._activeRunDebugSessionPaths.values()]
            .some(runPath => isMatchingAppHostPath(runPath, appHostPath));
    }
}

async function getLaunchTelemetryProperties(appHostPath: string, command: AspireCommandType, noDebug: boolean, executionSuppressed: boolean) {
    const isDirectory = isDirectoryForTelemetry(appHostPath);
    return {
        mode: noDebug ? 'run' : 'debug',
        command: bucketAspireCommand(command),
        apphost_language: isDirectory ? await classifyAppHostDirectory(appHostPath) : classifyAppHostPath(appHostPath),
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
    if (isSameFileSystemEntry(normalizedLeft, normalizedRight)) {
        return true;
    }

    return isSameFileSystemEntry(path.dirname(normalizedLeft), path.dirname(normalizedRight)) &&
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
