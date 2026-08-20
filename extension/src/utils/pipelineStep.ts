import * as vscode from 'vscode';
import { enterPipelineStep, pipelineStepRequired } from '../loc/strings';
import { CliPathResolutionTarget } from './cliPathVariables';
import { ConfigInfoProvider } from './configInfoProvider';

/**
 * Resolves the pipeline step for the exact CLI target that will execute it.
 * Returns null when the capable CLI should select the step through its interaction service.
 * Returns undefined when the user cancels the compatibility prompt.
 *
 * @param configInfoProvider The provider to probe for capabilities. Callers should pass the
 *   shared instance created at extension activation rather than constructing a fresh one, so
 *   back-to-back pipeline actions against the same CLI reuse its config/capability cache instead
 *   of each spawning another `aspire config info --json` process.
 */
export async function resolvePipelineStep(
    configInfoProvider: ConfigInfoProvider,
    target: CliPathResolutionTarget,
    cliPath: string,
    pipelineInteractionSupported?: boolean,
): Promise<string | null | undefined> {
    const isPipelineInteractionSupported = pipelineInteractionSupported
        ?? await configInfoProvider.hasCapability('pipelines', {
            target,
            cliPath,
            suppressErrors: true,
            forceRefresh: true,
        });
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
