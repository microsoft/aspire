import * as vscode from 'vscode';
import * as path from 'path';
import {
    AppHostDataRepository,
    AspireCliFailedError,
    AspireCliNotInstalledError,
    filterResourceCommandStatusOutput,
} from '../data/AppHostDataRepository';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    resourceCommandCliNotInstalled,
    resourceCommandFailed,
    resourceCommandFailedNoDetail,
    resourceCommandLogOpenFailed,
    resourceCommandOpenAppHostLog,
    resourceCommandOpenCliLog,
    resourceCommandOutputOpenFailed,
    resourceCommandRunning,
    resourceCommandSucceeded,
} from '../loc/strings';

// Narrow slice of AppHostDataRepository used to execute resource commands. Depending on the
// interface rather than the concrete repository keeps the executor easy to unit test with a fake.
export type ResourceCommandRunner = Pick<AppHostDataRepository, 'runResourceCommand'>;

// Renders a command's returned value in a VS Code editor. The CLI has already rendered the value
// (text/json/markdown) to plain text on stdout, so the renderer only needs to surface that text.
// Implemented by the tree provider's read-only `aspire-source` content provider.
export type ResourceCommandOutputRenderer = (resourceName: string, commandName: string, content: string, appHostPath?: string) => Promise<void> | void;

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

const failureDetailDisplayLimit = 2000;

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
            cancellable: true,
        },
        async (_progress, token) => {
            let output;
            try {
                output = await runner.runResourceCommand(
                    request.resourceName,
                    request.appHostPath,
                    request.commandName,
                    request.additionalArgs ?? [],
                    token);
            } catch (error) {
                if (isCancellationError(error)) {
                    throw error;
                }

                const hadOutput = await handleFailure(renderOutput, request, displayName, error);
                return { success: false, hadOutput };
            }

            vscode.window.showInformationMessage(resourceCommandSucceeded(request.commandName, displayName));
            const hadOutput = await tryRenderCommandOutput(renderOutput, request, output.stdout);
            return { success: true, hadOutput };
        });
}

async function handleFailure(
    renderOutput: ResourceCommandOutputRenderer,
    request: ResourceCommandExecutionRequest,
    displayName: string,
    error: unknown): Promise<boolean> {

    if (error instanceof AspireCliNotInstalledError) {
        extensionLogOutputChannel.error(`Failed to start the Aspire CLI for '${request.commandName}' on '${request.resourceName}': ${error.message}`);
        vscode.window.showErrorMessage(resourceCommandCliNotInstalled(error.message));
        return false;
    }

    if (error instanceof AspireCliFailedError) {
        const failure = getFailurePresentation(error, request);
        const detail = failure.detail;
        extensionLogOutputChannel.error(`Command '${request.commandName}' on '${request.resourceName}' failed: ${error.command} exited with code ${error.exitCode}.`);
        const message = detail
            ? getFailureMessage(request.commandName, displayName, detail)
            : resourceCommandFailedNoDetail(request.commandName, displayName);
        void showFailureNotification(message, failure);
        return await tryRenderCommandOutput(renderOutput, request, error.stdout);
    }

    const message = getErrorMessage(error);
    extensionLogOutputChannel.error(`Command '${request.commandName}' on '${request.resourceName}' failed: ${message}`);
    vscode.window.showErrorMessage(resourceCommandFailed(request.commandName, displayName, limitFailureDetailForDisplay(message)));
    return false;
}

async function tryRenderCommandOutput(
    renderOutput: ResourceCommandOutputRenderer,
    request: ResourceCommandExecutionRequest,
    stdout: string): Promise<boolean> {

    // Most lifecycle commands (start/stop/restart) return no value; only render when the command
    // produced output so we don't open empty editors for the common case.
    if (stdout.trim().length === 0) {
        return false;
    }

    try {
        await renderOutput(request.resourceName, request.commandName, stdout, request.appHostPath);
        return true;
    } catch (error) {
        const message = getErrorMessage(error);
        extensionLogOutputChannel.error(`Command '${request.commandName}' on '${request.resourceName}' completed, but output rendering failed: ${message}`);
        vscode.window.showErrorMessage(resourceCommandOutputOpenFailed(message));
        return false;
    }
}

async function openDiagnosticLog(filePath: string): Promise<void> {
    try {
        const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
        await vscode.window.showTextDocument(document, { preview: false });
    } catch (error) {
        vscode.window.showWarningMessage(resourceCommandLogOpenFailed(filePath, getErrorMessage(error)));
    }
}

export function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

interface FailurePresentation {
    detail?: string;
    cliLogPath?: string;
    appHostLogPath?: string;
}

async function showFailureNotification(message: string, failure: FailurePresentation): Promise<void> {
    try {
        const actions = [
            ...(failure.cliLogPath ? [resourceCommandOpenCliLog] : []),
            ...(failure.appHostLogPath ? [resourceCommandOpenAppHostLog] : []),
        ];
        const selectedAction = await vscode.window.showErrorMessage(message, ...actions);
        const selectedLogPath = selectedAction === resourceCommandOpenCliLog
            ? failure.cliLogPath
            : selectedAction === resourceCommandOpenAppHostLog
                ? failure.appHostLogPath
                : undefined;
        if (selectedLogPath) {
            await openDiagnosticLog(selectedLogPath);
        }
    } catch (error) {
        extensionLogOutputChannel.error(`Failed to handle resource command failure notification: ${getErrorMessage(error)}`);
    }
}

function getFailurePresentation(error: AspireCliFailedError, request: ResourceCommandExecutionRequest): FailurePresentation {
    const stderr = filterResourceCommandStatusOutput(error.stderr, request.resourceName, request.commandName);
    const stdout = filterResourceCommandStatusOutput(error.stdout, request.resourceName, request.commandName);
    const stderrLines = stderr.split(/\r?\n/);
    const detailLines: string[] = [];
    let cliLogPath: string | undefined;
    let appHostLogPath: string | undefined;

    for (const line of stderrLines) {
        const trimmedLine = line.trim();
        const parsedCliLogPath = getDiagnosticLogPath(trimmedLine, '📄', 'See logs at ');
        const parsedAppHostLogPath = getDiagnosticLogPath(trimmedLine, '🔍', 'See AppHost logs at ');
        if (parsedCliLogPath) {
            cliLogPath = parsedCliLogPath;
        } else if (parsedAppHostLogPath) {
            appHostLogPath = parsedAppHostLogPath;
        } else if (trimmedLine.length > 0) {
            detailLines.push(trimmedLine);
        }
    }

    detailLines.push(...stdout
        .split(/\r?\n/)
        .map(line => line.trim())
        .filter(line => line.length > 0));

    const detail = detailLines.join('\n');

    return {
        detail: detail.length > 0 ? detail : undefined,
        cliLogPath,
        appHostLogPath,
    };
}

function getDiagnosticLogPath(line: string, icon: string, englishPrefix: string): string | undefined {
    if (line.startsWith(icon)) {
        const text = line.slice(icon.length).trim();
        for (let index = 0; index < text.length; index++) {
            if (index > 0 && !/\s/u.test(text[index - 1])) {
                continue;
            }

            const candidate = text.slice(index).trim();
            if (path.isAbsolute(candidate)) {
                return candidate;
            }
        }

        return undefined;
    }

    if (line.startsWith(englishPrefix)) {
        const candidate = line.slice(englishPrefix.length).trim();
        return path.isAbsolute(candidate) ? candidate : undefined;
    }

    return undefined;
}

function getFailureMessage(commandName: string, displayName: string, detail: string): string {
    const boundedDetail = limitFailureDetailForDisplay(detail);
    return detail.startsWith('❌') || /^Failed to\b/u.test(detail)
        ? boundedDetail
        : resourceCommandFailed(commandName, displayName, boundedDetail);
}

function limitFailureDetailForDisplay(detail: string): string {
    return detail.length <= failureDetailDisplayLimit
        ? detail
        : `${detail.slice(0, failureDetailDisplayLimit).trimEnd()}\n...`;
}

function isCancellationError(error: unknown): boolean {
    return error instanceof vscode.CancellationError;
}
