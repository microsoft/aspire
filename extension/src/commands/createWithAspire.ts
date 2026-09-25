import * as vscode from 'vscode';
import {
    addAspireToWorkspaceDescription,
    addAspireToWorkspaceLabel,
    createNewAspireAppDescription,
    createNewAspireAppLabel,
    createWithAspirePlaceholder,
} from '../loc/strings';
import { type HandledCommandOutcome } from '../utils/telemetry';

interface CreateWithAspireItem extends vscode.QuickPickItem {
    readonly command: 'aspire-vscode.new' | 'aspire-vscode.init';
}

/**
 * Entry point for the Aspire pane's "Set up Aspire" action. Offers the two
 * existing creation workflows (aspire new / aspire init) using outcome-oriented
 * language rather than requiring the user to already know the CLI command names,
 * then delegates to the corresponding command so the CLI invocation, target
 * resolution, and telemetry stay owned by a single implementation.
 */
export async function createWithAspireCommand(): Promise<HandledCommandOutcome | undefined> {
    const items: CreateWithAspireItem[] = [
        {
            label: createNewAspireAppLabel,
            detail: createNewAspireAppDescription,
            command: 'aspire-vscode.new',
        },
        {
            label: addAspireToWorkspaceLabel,
            detail: addAspireToWorkspaceDescription,
            command: 'aspire-vscode.init',
        },
    ];

    const selected = await vscode.window.showQuickPick(items, {
        placeHolder: createWithAspirePlaceholder,
    });

    if (!selected) {
        throw new vscode.CancellationError();
    }

    if (selected.command === 'aspire-vscode.new') {
        return vscode.commands.executeCommand<HandledCommandOutcome | undefined>(selected.command, 'tree');
    }

    return vscode.commands.executeCommand<HandledCommandOutcome | undefined>(selected.command, undefined, 'tree');
}
