import * as vscode from 'vscode';
import { AppHostCliRunner, parseCliJsonOutput } from '../data/appHostCliRunner';
import { AspireCliFailedError, AspireCliParseError } from '../data/appHostCliContracts';
import { enterPipelineStep, loadingPipelineSteps, pipelineStepRequired, selectPipelineStep } from '../loc/strings';
import { CliPathResolutionTarget } from './cliPathVariables';
import { ConfigInfoProvider } from './configInfoProvider';

export interface PipelineStepInfo {
    name: string;
    description?: string;
    dependsOn: string[];
    tags: string[];
    resourceName?: string;
}

interface PipelineStepQuickPickItem extends vscode.QuickPickItem {
    step?: PipelineStepInfo;
}

const appHostIncompatibleExitCode = 9;

export async function selectPipelineStepFromCli(
    cliRunner: AppHostCliRunner,
    appHostPath: string,
    target: CliPathResolutionTarget,
    cliPath: string,
): Promise<string | undefined> {
    const args = cliRunner.withNoLogo([
        'do',
        '--list-steps',
        '--format',
        'json',
        '--apphost',
        appHostPath,
    ], cliPath);
    const { stdout } = await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: loadingPipelineSteps,
        cancellable: true,
    }, async (_, cancellationToken) => cliRunner.runCliCommand(
        'list pipeline steps',
        args,
        { target, cliPath, timeoutMs: null, cancellationToken }));
    const steps = parsePipelineSteps(stdout);

    if (steps.length === 0) {
        return promptForPipelineStep();
    }

    const items: PipelineStepQuickPickItem[] = steps.map(step => ({
        label: step.name,
        description: step.description,
        detail: step.resourceName,
        step,
    }));
    items.push({ label: enterPipelineStep });
    const selected = await vscode.window.showQuickPick(items, {
        placeHolder: selectPipelineStep,
        matchOnDescription: true,
        matchOnDetail: true,
    });

    return selected?.step?.name ?? (selected ? promptForPipelineStep() : undefined);
}

export function isPipelineStepListUnsupportedError(error: unknown): boolean {
    return error instanceof AspireCliFailedError && error.exitCode === appHostIncompatibleExitCode;
}

function parsePipelineSteps(stdout: string): PipelineStepInfo[] {
    const value = parseCliJsonOutput<unknown>(stdout);
    if (!Array.isArray(value) || !value.every(isPipelineStepInfo)) {
        throw new AspireCliParseError('list pipeline steps', stdout, new Error('The response does not contain valid pipeline step metadata.'));
    }

    return value;
}

function isPipelineStepInfo(value: unknown): value is PipelineStepInfo {
    if (!isRecord(value)
        || typeof value.name !== 'string'
        || !Array.isArray(value.dependsOn)
        || !value.dependsOn.every(item => typeof item === 'string')
        || !Array.isArray(value.tags)
        || !value.tags.every(item => typeof item === 'string')) {
        return false;
    }

    return (value.description === undefined || typeof value.description === 'string')
        && (value.resourceName === undefined || typeof value.resourceName === 'string');
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null;
}

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

    return promptForPipelineStep();
}

async function promptForPipelineStep(): Promise<string | undefined> {
    const step = await vscode.window.showInputBox({
        prompt: enterPipelineStep,
        placeHolder: 'deploy',
        validateInput: value => value.trim() ? undefined : pipelineStepRequired,
    });

    return step?.trim();
}
