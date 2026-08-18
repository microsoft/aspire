import * as vscode from 'vscode';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { ConfigInfoProvider } from './configInfoProvider';
import { enterPipelineStep } from '../loc/strings';

/**
 * Checks CLI capabilities to determine whether the CLI supports interactive pipeline prompting.
 * Returns null if the CLI will handle prompting (new CLI with pipelines capability).
 * Returns the user-provided step name if the CLI doesn't support interactive prompting (old CLI).
 * Returns undefined if the user cancels.
 */
export async function resolvePipelineStep(terminalProvider: AspireTerminalProvider): Promise<string | null | undefined> {
    const configInfoProvider = new ConfigInfoProvider(terminalProvider);
    if (await configInfoProvider.hasCapability('pipelines')) {
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
