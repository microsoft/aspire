import * as vscode from 'vscode';
import {
    AppHostDataRepository,
    AspireCliFailedError,
    AspireCliNotInstalledError,
} from './AppHostDataRepository';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    resourceCommandCliNotInstalled,
    resourceCommandFailed,
    resourceCommandFailedNoDetail,
    resourceCommandRunning,
    resourceCommandSucceeded,
} from '../loc/strings';

// Narrow slice of AppHostDataRepository used to execute resource commands. Depending on the
// interface rather than the concrete repository keeps the executor easy to unit test with a fake.
export type ResourceCommandRunner = Pick<AppHostDataRepository, 'runResourceCommand'>;

// Renders a command's returned value in a VS Code editor. The CLI has already rendered the value
// (text/json/markdown) to plain text on stdout, so the renderer only needs to surface that text.
// Implemented by the tree provider's read-only `aspire-source` content provider.
export type ResourceCommandOutputRenderer = (resourceName: string, commandName: string, content: string) => Promise<void> | void;

export interface ResourceCommandExecutionRequest {
    resourceName: string;
    commandName: string;
    // User-facing name shown in messages. Falls back to resourceName when omitted.
    displayName?: string;
    // Absolute AppHost path, or undefined to let the CLI resolve the running AppHost.
    appHostPath?: string;
    // Extra CLI tokens collected from argument prompts (already include the `--` delimiter).
    additionalArgs?: readonly string[];
}

export interface ResourceCommandExecutionOutcome {
    success: boolean;
    hadOutput: boolean;
}

/**
 * Executes a resource command through the hidden `aspire resource ...` backchannel path and reports
 * the result entirely inside VS Code: a progress notification while it runs, a success/failure
 * message, and a read-only editor for any value the command returns. This replaces the previous
 * behavior of typing the command into the visible Aspire terminal where output was only visible as
 * raw stdout.
 */
export async function executeResourceCommand(
    runner: ResourceCommandRunner,
    renderOutput: ResourceCommandOutputRenderer,
    request: ResourceCommandExecutionRequest): Promise<ResourceCommandExecutionOutcome> {

    const displayName = request.displayName ?? request.resourceName;

    return await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: resourceCommandRunning(request.commandName, displayName),
            cancellable: false,
        },
        async () => {
            try {
                const output = await runner.runResourceCommand(
                    request.resourceName,
                    request.appHostPath,
                    request.commandName,
                    request.additionalArgs ?? []);

                vscode.window.showInformationMessage(resourceCommandSucceeded(request.commandName, displayName));
                const hadOutput = await renderCommandOutput(renderOutput, request, output.stdout);
                return { success: true, hadOutput };
            } catch (error) {
                return await handleFailure(renderOutput, request, displayName, error);
            }
        });
}

async function handleFailure(
    renderOutput: ResourceCommandOutputRenderer,
    request: ResourceCommandExecutionRequest,
    displayName: string,
    error: unknown): Promise<ResourceCommandExecutionOutcome> {

    if (error instanceof AspireCliNotInstalledError) {
        extensionLogOutputChannel.error(`Failed to start the Aspire CLI for '${request.commandName}' on '${request.resourceName}': ${error.message}`);
        vscode.window.showErrorMessage(resourceCommandCliNotInstalled(error.message));
        return { success: false, hadOutput: false };
    }

    if (error instanceof AspireCliFailedError) {
        // The CLI routes human-readable command status/errors to stderr (stdout is reserved for the
        // structured command value), so prefer stderr for the surfaced message and fall back to
        // stdout when stderr is empty.
        const detail = (error.stderr || error.stdout || '').trim();
        extensionLogOutputChannel.error(`Command '${request.commandName}' on '${request.resourceName}' failed: ${error.message}`);
        vscode.window.showErrorMessage(detail
            ? resourceCommandFailed(request.commandName, displayName, firstLine(detail))
            : resourceCommandFailedNoDetail(request.commandName, displayName));
        const hadOutput = await renderCommandOutput(renderOutput, request, error.stdout);
        return { success: false, hadOutput };
    }

    const message = getErrorMessage(error);
    extensionLogOutputChannel.error(`Command '${request.commandName}' on '${request.resourceName}' failed: ${message}`);
    vscode.window.showErrorMessage(resourceCommandFailed(request.commandName, displayName, firstLine(message)));
    return { success: false, hadOutput: false };
}

async function renderCommandOutput(
    renderOutput: ResourceCommandOutputRenderer,
    request: ResourceCommandExecutionRequest,
    stdout: string): Promise<boolean> {

    // Most lifecycle commands (start/stop/restart) return no value; only render when the command
    // produced output so we don't open empty editors for the common case.
    if (stdout.trim().length === 0) {
        return false;
    }

    await renderOutput(request.resourceName, request.commandName, stdout);
    return true;
}

function firstLine(value: string): string {
    const newlineIndex = value.indexOf('\n');
    return newlineIndex === -1 ? value : value.slice(0, newlineIndex).trimEnd();
}

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
