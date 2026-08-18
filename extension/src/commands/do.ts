import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { noAppHostInWorkspace } from '../loc/strings';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';
import { resolvePipelineStep } from '../utils/pipelineStep';

export async function doCommand(
    terminalProvider: AspireTerminalProvider,
    editorCommandProvider: AspireEditorCommandProvider,
    appHostPath: string | undefined,
    target: CliPathResolutionTarget,
    cliPath: string,
) {
    if (!appHostPath) {
        vscode.window.showErrorMessage(noAppHostInWorkspace);
        throw new vscode.CancellationError();
    }

    const step = await resolvePipelineStep(terminalProvider, target, cliPath);
    if (step === undefined) {
        throw new vscode.CancellationError();
    }
    await editorCommandProvider.tryExecuteDoAppHost(false, step ?? undefined, appHostPath, target, cliPath);
}
