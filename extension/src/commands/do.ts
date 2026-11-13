import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { noAppHostInWorkspace } from '../loc/strings';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { resolvePipelineStep } from '../utils/pipelineStep';

export async function doCommand(
    configInfoProvider: ConfigInfoProvider,
    editorCommandProvider: AspireEditorCommandProvider,
    appHostPath: string | undefined,
    target: CliPathResolutionTarget,
    cliPath: string,
) {
    if (!appHostPath) {
        vscode.window.showErrorMessage(noAppHostInWorkspace);
        throw new vscode.CancellationError();
    }

    const step = await resolvePipelineStep(configInfoProvider, target, cliPath);
    if (step === undefined) {
        throw new vscode.CancellationError();
    }
    await editorCommandProvider.tryExecuteDoAppHost(false, step ?? undefined, appHostPath, target, cliPath);
}
