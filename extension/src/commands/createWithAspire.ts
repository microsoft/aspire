import * as vscode from 'vscode';
import {
    addAspireToWorkspaceDescription,
    addAspireToWorkspaceLabel,
    createNewAspireAppDescription,
    createNewAspireAppLabel,
    createWithAspirePlaceholder,
    errorMessage,
} from '../loc/strings';
import { type HandledCommandOutcome, isCommandCancellation } from '../utils/telemetry';
import type { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { extensionLogOutputChannel } from '../utils/logging';
import { selectCommandTarget } from '../utils/workspace';

interface CreateWithAspireItem extends vscode.QuickPickItem {
    readonly command: 'aspire-vscode.new' | 'aspire-vscode.init';
}

/**
 * Entry point for the Aspire pane's "Set up Aspire" action. Offers the two
 * existing creation workflows (aspire new / aspire init) using outcome-oriented
 * language rather than requiring the user to already know the CLI command names,
 * then delegates to the corresponding command so the CLI invocation, target
 * resolution, and telemetry stay owned by a single implementation. Initialization
 * is restricted to folders with no AppHost candidates, even when existing
 * candidates are unbuildable or have no selected default.
 */
export async function createWithAspireCommand(appHostDiscoveryService: AppHostDiscoveryService): Promise<HandledCommandOutcome | undefined> {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const discoveryResults = await Promise.allSettled(folders.map(folder => appHostDiscoveryService.discover(folder)));
    const eligibleFolders: vscode.WorkspaceFolder[] = [];
    for (const [index, result] of discoveryResults.entries()) {
        if (result.status === 'rejected') {
            const error: unknown = result.reason;
            if (isCommandCancellation(error)) {
                throw error;
            }

            // Failed discovery is not evidence that a folder has no AppHost.
            extensionLogOutputChannel.warn(`Failed to discover AppHost candidates for workspace ${folders[index].uri.fsPath}: ${error}`);
        }
        else if (result.value.length === 0) {
            eligibleFolders.push(folders[index]);
        }
    }
    const failedDiscovery = discoveryResults.find(result => result.status === 'rejected');
    if (failedDiscovery?.status === 'rejected') {
        void vscode.window.showErrorMessage(errorMessage(failedDiscovery.reason));
    }

    const items: CreateWithAspireItem[] = [
        {
            label: createNewAspireAppLabel,
            detail: createNewAspireAppDescription,
            command: 'aspire-vscode.new',
        },
    ];

    if (folders.length === 0 || eligibleFolders.length > 0) {
        items.push({
            label: addAspireToWorkspaceLabel,
            detail: addAspireToWorkspaceDescription,
            command: 'aspire-vscode.init',
        });
    }

    const selected = await vscode.window.showQuickPick(items, {
        placeHolder: createWithAspirePlaceholder,
    });

    if (!selected) {
        throw new vscode.CancellationError();
    }

    if (selected.command === 'aspire-vscode.new') {
        return vscode.commands.executeCommand<HandledCommandOutcome | undefined>(selected.command, 'tree');
    }

    const target = await selectCommandTarget(eligibleFolders);
    return vscode.commands.executeCommand<HandledCommandOutcome | undefined>(selected.command, target, 'tree');
}
