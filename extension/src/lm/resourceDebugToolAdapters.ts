import * as vscode from 'vscode';

import {
    resourceDebugToolConfirmationMessage,
    resourceDebugToolConfirmationTitle,
    resourceDebugToolInvocationMessage,
    resourceDebugToolUnresolvedConfirmationMessage,
    resourceDebugToolUnavailableInvocationMessage,
} from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    aspireResourceDebugToolName,
    type AspireResourceDebugToolInput,
    type AspireResourceDebugToolRegistration,
    type AspireResourceDebugToolResult,
} from './resourceDebugToolContracts';
import { AspireResourceDebugToolService } from './resourceDebugToolService';
import { escapeMarkdownForConfirmation } from './markdown';

export class AspireResourceDebugLanguageModelTool implements vscode.LanguageModelTool<AspireResourceDebugToolInput> {
    constructor(private readonly _service: AspireResourceDebugToolService) {
    }

    async prepareInvocation(
        options: vscode.LanguageModelToolInvocationPrepareOptions<AspireResourceDebugToolInput>,
        token: vscode.CancellationToken,
    ): Promise<vscode.PreparedToolInvocation> {
        const preparation = await this._service.prepare(options.input, token);
        if (!preparation.canDebug) {
            // Do not let a transient discovery failure bypass VS Code's confirmation step.
            // The generic message contains no model input or unresolved target; invocation
            // resolves again and still applies trust and validation checks.
            return {
                invocationMessage: resourceDebugToolUnavailableInvocationMessage,
                confirmationMessages: {
                    title: resourceDebugToolConfirmationTitle,
                    message: resourceDebugToolUnresolvedConfirmationMessage,
                },
            };
        }

        const resourceName = escapeMarkdownForConfirmation(preparation.resourceName);
        const appHost = escapeMarkdownForConfirmation(preparation.target.displayPath);
        return {
            invocationMessage: resourceDebugToolInvocationMessage(resourceName),
            confirmationMessages: {
                title: resourceDebugToolConfirmationTitle,
                message: resourceDebugToolConfirmationMessage(resourceName, appHost),
            },
        };
    }

    async invoke(
        options: vscode.LanguageModelToolInvocationOptions<AspireResourceDebugToolInput>,
        token: vscode.CancellationToken,
    ): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._service.debug(options.input, token));
    }
}

export function registerAspireResourceDebugTool(service: AspireResourceDebugToolService): AspireResourceDebugToolRegistration {
    const registrations: vscode.Disposable[] = [];
    const tool = new AspireResourceDebugLanguageModelTool(service);
    const tools = new Map([
        [aspireResourceDebugToolName, {
            prepareInvocation: (options: { readonly input: Record<string, unknown> }, token: vscode.CancellationToken) =>
                tool.prepareInvocation({ input: options.input as unknown as AspireResourceDebugToolInput }, token),
            invoke: (
                options: { readonly input: Record<string, unknown>; readonly toolInvocationToken: undefined },
                token: vscode.CancellationToken,
            ) => tool.invoke({
                input: options.input as unknown as AspireResourceDebugToolInput,
                toolInvocationToken: options.toolInvocationToken,
            }, token),
        }],
    ]);

    if (typeof vscode.lm?.registerTool !== 'function') {
        extensionLogOutputChannel.info('Skipping Aspire resource debug language model tool: the language model tool API is unavailable.');
    }
    else {
        registrations.push(vscode.lm.registerTool(aspireResourceDebugToolName, tool));
        extensionLogOutputChannel.info('Registered Aspire resource debug language model tool.');
    }

    return {
        get registered() {
            return registrations.length > 0;
        },
        tools,
        dispose() {
            registrations.forEach(registration => registration.dispose());
            registrations.length = 0;
        },
    };
}

function createToolResult(result: AspireResourceDebugToolResult): vscode.LanguageModelToolResult {
    return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(JSON.stringify(result))]);
}
