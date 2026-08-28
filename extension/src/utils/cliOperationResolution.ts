import * as vscode from 'vscode';
import { CliPathResolutionTarget } from './cliPathVariables';

export interface CliOperationResolution {
    target: CliPathResolutionTarget;
    cliPath: string;
}

const cliOperationResolutionRefreshIntervalMs = 5 * 60 * 1_000;
const cliOperationResolutionEmitter = new vscode.EventEmitter<CliOperationResolution>();

/**
 * Fires after an Aspire operation has selected the exact CLI executable it will invoke.
 * Resolution performed only for activation-time environment setup is intentionally excluded.
 */
export const onDidResolveCliForOperation = cliOperationResolutionEmitter.event;

export function reportCliResolvedForOperation(target: CliPathResolutionTarget, cliPath: string): void {
    cliOperationResolutionEmitter.fire({ target, cliPath });
}

/**
 * Re-reports a CLI while its long-lived operation remains active so version-cache expiry can
 * observe an executable replaced in place without probing paths whose operations have stopped.
 */
export function startCliOperationResolutionHeartbeat(
    target: CliPathResolutionTarget,
    cliPath: string,
    isActive: () => boolean,
): vscode.Disposable {
    let stopped = false;
    const stop = () => {
        if (!stopped) {
            stopped = true;
            clearInterval(timer);
        }
    };
    const timer = setInterval(() => {
        if (!isActive()) {
            stop();
            return;
        }
        reportCliResolvedForOperation(target, cliPath);
    }, cliOperationResolutionRefreshIntervalMs);
    return new vscode.Disposable(stop);
}
