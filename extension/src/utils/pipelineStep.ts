import * as vscode from 'vscode';
import { enterPipelineStep, pipelineStepRequired } from '../loc/strings';
import { CliPathResolutionTarget } from './cliPathVariables';
import { ConfigInfoProvider } from './configInfoProvider';

/**
 * Resolves the pipeline step for the exact CLI target that will execute it.
 * Returns null when the capable CLI should select the step through its interaction service.
 * Returns undefined when the user cancels the compatibility prompt.
 */
export async function resolvePipelineStep(
    configInfoProvider: ConfigInfoProvider,
    target: CliPathResolutionTarget,
    cliPath: string,
    pipelineInteractionSupported?: boolean,
): Promise<string | null | undefined> {
    const isPipelineInteractionSupported = pipelineInteractionSupported
        ?? await configInfoProvider.hasCapability('pipelines', { target, cliPath, suppressErrors: true });
    if (isPipelineInteractionSupported) {
        return null;
    }

    const step = await vscode.window.showInputBox({
        prompt: enterPipelineStep,
        placeHolder: 'deploy',
        validateInput: value => value.trim() ? undefined : pipelineStepRequired,
    });

    return step?.trim();
}
