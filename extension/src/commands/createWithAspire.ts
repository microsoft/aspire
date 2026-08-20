import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import {
    addAspireToWorkspaceDescription,
    addAspireToWorkspaceLabel,
    createNewAspireAppDescription,
    createNewAspireAppLabel,
    createWithAspirePlaceholder,
} from '../loc/strings';

interface CreateWithAspireItem extends vscode.QuickPickItem {
    readonly command: 'aspire-vscode.new' | 'aspire-vscode.init';
}

/**
 * Entry point for the Aspire pane's "Create with Aspire…" action. Offers the two
 * existing creation workflows (aspire new / aspire init) using outcome-oriented
 * language rather than requiring the user to already know the CLI command names,
 * then delegates to the corresponding command so the CLI invocation, target
 * resolution, and telemetry stay owned by a single implementation.
 */
export async function createWithAspireCommand(editorCommandProvider: AspireEditorCommandProvider): Promise<void> {
    const items: CreateWithAspireItem[] = [
        {
            label: createNewAspireAppLabel,
            detail: createNewAspireAppDescription,
            command: 'aspire-vscode.new',
        },
    ];

    // Only offer to add Aspire to the workspace when there's an applicable folder
    // that doesn't already have an AppHost — initializing on top of an existing
    // AppHost isn't a meaningful action.
    if (await editorCommandProvider.hasWorkspaceFolderWithoutAppHost()) {
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
        return;
    }

    // Reuse the existing registered commands rather than duplicating creation
    // logic — they already own target/CLI-path resolution and telemetry.
    await vscode.commands.executeCommand(selected.command);
}
