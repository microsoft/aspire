import * as vscode from 'vscode';
import * as path from 'path';
import { noWorkspaceFolder, aspireConfigExists, failedToConfigureLaunchJson, defaultConfigurationName } from '../loc/strings';

export async function configureLaunchJsonCommand() {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0]!;
    const launchJsonPath = path.join(workspaceFolder.uri.fsPath, '.vscode', 'launch.json');

    try {
        const defaultConfig = {
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: '${workspaceFolder}'
        };

        // Check if .vscode directory exists, create if not
        const vscodeDir = path.join(workspaceFolder.uri.fsPath, '.vscode');
        const vscodeUri = vscode.Uri.file(vscodeDir);

        try {
            await vscode.workspace.fs.stat(vscodeUri);
        } catch {
            // Directory doesn't exist, create it
            await vscode.workspace.fs.createDirectory(vscodeUri);
        }

        const launchUri = vscode.Uri.file(launchJsonPath);
        let launchConfig: any = {
            version: '0.2.0',
            configurations: []
        };

        // Check if launch.json already exists
        try {
            const existingContent = await vscode.workspace.fs.readFile(launchUri);
            const existingText = Buffer.from(existingContent).toString('utf8');
            launchConfig = JSON.parse(existingText);

            // Check if Aspire configuration already exists
            const hasAspireConfig = launchConfig.configurations?.some((config: any) =>
                config.type === 'aspire' && config.name === defaultConfigurationName
            );

            if (hasAspireConfig) {
                vscode.window.showInformationMessage(aspireConfigExists);
                vscode.window.showTextDocument(await vscode.workspace.openTextDocument(launchUri));
                return;
            }
        } catch {
            // File doesn't exist or is invalid JSON, we'll create/overwrite it
        }

        // Ensure configurations array exists
        if (!launchConfig.configurations) {
            launchConfig.configurations = [];
        }

        // Add the Aspire configuration
        launchConfig.configurations.push(defaultConfig);

        // Write the updated launch.json
        const updatedContent = JSON.stringify(launchConfig, null, 2);
        await vscode.workspace.fs.writeFile(launchUri, Buffer.from(updatedContent, 'utf8'));

        const document = await vscode.workspace.openTextDocument(launchUri);
        await vscode.window.showTextDocument(document);


    } catch (error) {
        vscode.window.showErrorMessage(failedToConfigureLaunchJson(error));
    }
}
