import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { enterPipelineStep, noAppHostInWorkspace } from '../loc/strings';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

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

    const step = await resolveStep(terminalProvider, target, cliPath);
    if (step === undefined) {
        throw new vscode.CancellationError();
    }
    await editorCommandProvider.tryExecuteDoAppHost(false, step ?? undefined, appHostPath, target, cliPath);
}

/**
 * Checks CLI capabilities to determine whether the CLI supports interactive pipeline prompting.
 * Returns null if the CLI will handle prompting (new CLI with pipelines capability).
 * Returns the user-provided step name if the CLI doesn't support interactive prompting (old CLI).
 * Returns undefined if the user cancels.
 */
async function resolveStep(terminalProvider: AspireTerminalProvider, target: CliPathResolutionTarget, cliPath: string): Promise<string | null | undefined> {
    const configInfoProvider = new ConfigInfoProvider(terminalProvider);
    if (await configInfoProvider.hasCapability('pipelines', { target, cliPath })) {
        // New CLI: it will prompt for the step via interaction service
        return null;
    }

    // Old CLI or capabilities unavailable: prompt the user for a step
    const step = await vscode.window.showInputBox({
        prompt: enterPipelineStep,
        placeHolder: 'deploy',
    });
    return step;
}
