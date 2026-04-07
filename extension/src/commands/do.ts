import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getConfigInfo } from '../utils/configInfoProvider';
import { enterPipelineStep } from '../loc/strings';

export async function doCommand(terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider) {
    const step = await resolveStep(terminalProvider);
    if (step === undefined) {
        return;
    }
    await editorCommandProvider.tryExecuteDoAppHost(false, step ?? undefined);
}

/**
 * Checks CLI capabilities to determine whether the CLI supports interactive pipeline prompting.
 * Returns null if the CLI will handle prompting (new CLI with pipelines capability).
 * Returns the user-provided step name if the CLI doesn't support interactive prompting (old CLI).
 * Returns undefined if the user cancels.
 */
async function resolveStep(terminalProvider: AspireTerminalProvider): Promise<string | null | undefined> {
    const configInfo = await getConfigInfo(terminalProvider);
    if (configInfo?.Capabilities?.includes('pipelines')) {
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
