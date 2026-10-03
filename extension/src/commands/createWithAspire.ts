import * as vscode from 'vscode';
import {
    addAspireToWorkspaceDescription,
    addAspireToWorkspaceLabel,
    createNewAspireAppDescription,
    createNewAspireAppLabel,
    createWithAspirePlaceholder,
} from '../loc/strings';
import { type HandledCommandOutcome } from '../utils/telemetry';
import type { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

interface CreateWithAspireItem extends vscode.QuickPickItem {
    readonly command: 'aspire-vscode.new' | 'aspire-vscode.init';
}

/**
 * True when every open workspace folder already has an AppHost, meaning
 * "Add Aspire to this workspace" (aspire init) has nothing left to
 * initialize. An empty workspace (no folders open) is never considered
 * fully set up, since init is still the applicable next step once a folder
 * is opened.
 */
async function workspaceFoldersAllHaveAppHost(editorCommandProvider: AspireEditorCommandProvider): Promise<boolean> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        return false;
    }

    const appHostPaths = await Promise.all(folders.map(folder => editorCommandProvider.getAppHostPath(folder.uri)));
    return appHostPaths.every(appHostPath => appHostPath !== null);
}

/**
 * Entry point for the Aspire pane's "Set up Aspire" action. Offers the two
 * existing creation workflows (aspire new / aspire init) using outcome-oriented
 * language rather than requiring the user to already know the CLI command names,
 * then delegates to the corresponding command so the CLI invocation, target
 * resolution, and telemetry stay owned by a single implementation.
 *
 * "Add Aspire to this workspace" is omitted once every workspace folder
 * already contains an AppHost, since offering to initialize Aspire again
 * would be a no-op.
 */
export async function createWithAspireCommand(editorCommandProvider: AspireEditorCommandProvider): Promise<HandledCommandOutcome | undefined> {
    const items: CreateWithAspireItem[] = [
        {
            label: createNewAspireAppLabel,
            detail: createNewAspireAppDescription,
            command: 'aspire-vscode.new',
        },
    ];

    if (!(await workspaceFoldersAllHaveAppHost(editorCommandProvider))) {
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

    return vscode.commands.executeCommand<HandledCommandOutcome | undefined>(selected.command, undefined, 'tree');
}
