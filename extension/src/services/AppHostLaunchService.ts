import * as path from 'path';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration } from '../dcp/types';

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
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
            }
        });
    }

    dispose(): void {
        this._debugSessionSubscription.dispose();
        this._onDidChangeLaunchingState.dispose();
    }

    /**
     * Returns whether the given AppHost path is currently in a launching state.
     */
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

        try {
            const started = await vscode.debug.startDebugging(undefined, config);
            if (!started) {
                this.clearLaunching(appHostPath);
            }
        } catch (err) {
            this.clearLaunching(appHostPath);
            throw err;
        }
    }
}
