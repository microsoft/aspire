import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import {
    addAspireToWorkspaceDescription,
    addAspireToWorkspaceLabel,
    createNewAspireAppDescription,
    createNewAspireAppLabel,
    createWithAspirePlaceholder,
    selectWorkspaceFolderForAspireCommand,
} from '../loc/strings';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

interface CreateWithAspireItem extends vscode.QuickPickItem {
    readonly command: 'aspire-vscode.new' | 'aspire-vscode.init';
}

interface WorkspaceFolderItem extends vscode.QuickPickItem {
    readonly workspaceFolder: vscode.WorkspaceFolder;
}

/**
 * Entry point for the Aspire pane's "Create with Aspire…" action. Offers the two
 * existing creation workflows (aspire new / aspire init) using outcome-oriented
 * language rather than requiring the user to already know the CLI command names,
 * then delegates to the corresponding command so the CLI invocation, target
 * resolution, and telemetry stay owned by a single implementation.
 */
export async function createWithAspireCommand(editorCommandProvider: AspireEditorCommandProvider): Promise<void> {
    const eligibleFolders = await editorCommandProvider.getWorkspaceFoldersWithoutAppHosts();
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
    if (eligibleFolders.length > 0) {
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

    if (selected.command === 'aspire-vscode.new') {
        await vscode.commands.executeCommand(selected.command);
        return;
    }

    const activeEditorUri = vscode.window.activeTextEditor?.document.uri;
    const activeWorkspaceFolder = activeEditorUri ? vscode.workspace.getWorkspaceFolder(activeEditorUri) : undefined;
    let folder = activeWorkspaceFolder
        ? eligibleFolders.find(candidate => candidate.uri.toString() === activeWorkspaceFolder.uri.toString())
        : undefined;

    if (!folder && eligibleFolders.length === 1) {
        folder = eligibleFolders[0];
    }

    if (!folder) {
        const folderItems: WorkspaceFolderItem[] = eligibleFolders.map(workspaceFolder => ({
            label: workspaceFolder.name,
            description: workspaceFolder.uri.fsPath,
            workspaceFolder,
        }));
        const selectedFolder = await vscode.window.showQuickPick(folderItems, {
            placeHolder: selectWorkspaceFolderForAspireCommand,
        });
        if (!selectedFolder) {
            return;
        }
        folder = selectedFolder.workspaceFolder;
    }

    await vscode.commands.executeCommand(selected.command, workspaceFolderCliPathTarget(folder));
}
