import type { AspireExtensionE2EControlCommand } from '../../types/extensionApi';
import { executeE2eControlCommand } from './fixtures';
import { acceptModalDialog, type AcceptedModalDialog } from './vscode';

export interface PreparedLanguageModelToolInvocation {
    invocationMessage?: string;
    confirmationTitle?: string;
    confirmationMessage?: string;
}

export interface LanguageModelToolInvocationOptions {
    expectedConfirmations?: number;
    confirmationButtonTitle?: string;
    screenshotName?: string;
    timeoutMs?: number;
    times?: number;
    cancelAfterMs?: number;
}

export interface LanguageModelToolInvocation<T> {
    results: T[];
    dialogs: AcceptedModalDialog[];
    cancelled: boolean;
}

export async function prepareLanguageModelToolInvocation(
    toolName: string,
    input: Record<string, unknown>,
    timeoutMs = 120000,
): Promise<PreparedLanguageModelToolInvocation> {
    return await invokeControlCommand<PreparedLanguageModelToolInvocation>({
        name: 'prepareLanguageModelToolInvocation',
        toolName,
        input,
    }, timeoutMs);
}

/**
 * Drives any registered language-model tool through VS Code's public invocation API.
 * Invocation begins before confirmation is accepted because `vscode.lm.invokeTool` waits
 * for the modal. The state bridge stores only the tool's bounded text result.
 */
export async function invokeLanguageModelTool<T>(
    toolName: string,
    input: Record<string, unknown>,
    options: LanguageModelToolInvocationOptions = {},
): Promise<LanguageModelToolInvocation<T>> {
    const expectedConfirmations = options.expectedConfirmations ?? 1;
    const invocation = invokeControlCommand<{ results: string[]; cancelled?: boolean }>({
        name: 'invokeLanguageModelTool',
        toolName,
        input,
        times: options.times,
        cancelAfterMs: options.cancelAfterMs,
    }, options.timeoutMs ?? 120000);
    invocation.catch(() => undefined);

    const dialogs: AcceptedModalDialog[] = [];
    let invocationSettled = false;
    void invocation.finally(() => invocationSettled = true).catch(() => undefined);
    for (let index = 0; index < expectedConfirmations; index++) {
        const buttonTitle = options.confirmationButtonTitle ?? 'Yes';
        const screenshotName = index === 0 ? options.screenshotName : undefined;
        if (options.cancelAfterMs === undefined) {
            dialogs.push(await acceptModalDialog(buttonTitle, 180000, screenshotName));
            continue;
        }

        // Cancellation can win before VS Code creates the confirmation dialog, or it can leave
        // an already-open dialog waiting for acknowledgement. Probe while the invocation is
        // pending so either ordering completes without leaving a modal for the next test.
        const deadline = Date.now() + 180000;
        let confirmationAccepted = false;
        while (!invocationSettled) {
            const remainingMs = deadline - Date.now();
            if (remainingMs <= 0) {
                throw new Error(`Timed out waiting for the cancelled language-model invocation or a '${buttonTitle}' confirmation.`);
            }

            try {
                dialogs.push(await acceptModalDialog(buttonTitle, Math.min(1000, remainingMs), screenshotName));
                confirmationAccepted = true;
                break;
            }
            catch {
                // A confirmation is optional once the invocation has observed cancellation.
            }
        }

        if (!confirmationAccepted && invocationSettled) {
            try {
                dialogs.push(await acceptModalDialog(buttonTitle, 1000, screenshotName));
            }
            catch {
                // The invocation completed before VS Code created a confirmation.
            }
        }
    }

    const result = await invocation;
    return {
        results: result.results.map(item => JSON.parse(item) as T),
        dialogs,
        cancelled: result.cancelled === true,
    };
}

async function invokeControlCommand<T>(
    command: AspireExtensionE2EControlCommand,
    timeoutMs: number,
): Promise<T> {
    const status = await executeE2eControlCommand(command, { timeoutMs });
    if (status.errorMessage) {
        throw new Error(`E2E control command '${command.name}' failed: ${status.errorMessage}`);
    }

    return status.result as T;
}
