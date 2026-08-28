import * as vscode from 'vscode';
import { CliPathResolutionTarget } from './cliPathVariables';

export interface CliOperationResolution {
    target: CliPathResolutionTarget;
    cliPath: string;
}

const cliOperationResolutionEmitter = new vscode.EventEmitter<CliOperationResolution>();

/**
 * Fires after an Aspire operation has selected the exact CLI executable it will invoke.
 * Resolution performed only for activation-time environment setup is intentionally excluded.
 */
export const onDidResolveCliForOperation = cliOperationResolutionEmitter.event;

export function reportCliResolvedForOperation(target: CliPathResolutionTarget, cliPath: string): void {
    cliOperationResolutionEmitter.fire({ target, cliPath });
}
