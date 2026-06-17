import * as path from 'path';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration } from '../dcp/types';

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}

export interface AppHostLaunchRequestedEvent {
    appHostPath: string;
    command: AspireCommandType;
    noDebug: boolean;
    doStep?: string;
    executionSuppressed: boolean;
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

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<string>();
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
                this._onDidTerminateAppHostDebugSession.fire(appHostPath);
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
    async launch(appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep?: string): Promise<void> {
        // Track launching state before awaiting startDebugging so the tree shows "Starting..."
        // immediately. We must clear this state if startDebugging returns false (debug adapter
        // rejected, no provider matched, user cancelled) or throws — otherwise no terminate
        // event will fire and the tree item stays stuck on the spinner indefinitely.
        // See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
        this._launchingPaths.add(getComparisonKey(path.resolve(appHostPath)));
        this._onDidChangeLaunchingState.fire();

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

        const executionSuppressed = isE2eDebugLaunchSuppressed();
        this._onDidRequestLaunch.fire({
            appHostPath,
            command,
            noDebug,
            doStep,
            executionSuppressed,
        });

        if (executionSuppressed) {
            this.clearLaunching(appHostPath);
            return;
        }

        try {
            const started = await vscode.debug.startDebugging(undefined, config);
            if (!started) {
                throw new Error(`VS Code did not start the Aspire ${command} session for ${vscode.workspace.asRelativePath(appHostPath)}.`);
            }
        } catch (err) {
            this.clearLaunching(appHostPath);
            throw err;
        }
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
