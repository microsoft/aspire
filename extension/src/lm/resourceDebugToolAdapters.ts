import * as vscode from 'vscode';

import {
    resourceDebugToolConfirmationMessage,
    resourceDebugToolConfirmationTitle,
    resourceDebugToolInvocationMessage,
    resourceDebugToolUnresolvedConfirmationMessage,
    resourceDebugToolUnavailableInvocationMessage,
} from '../loc/strings';
import {
    aspireResourceDebugToolName,
    type AspireResourceDebugToolInput,
    type AspireResourceDebugToolRegistration,
} from './resourceDebugToolContracts';
import { AspireResourceDebugToolService } from './resourceDebugToolService';
import { escapeMarkdownForConfirmation } from './markdown';
import { createJsonToolResult } from './languageModelToolUi';
import { registerLanguageModelTools } from './languageModelToolRegistration';

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
            // The generic message contains no model input or unresolved target, and cannot
            // authorize a newly resolved AppHost without a fresh resolved confirmation.
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
        return createJsonToolResult(await this._service.debug(options.input, token));
    }
}

export function registerAspireResourceDebugTool(service: AspireResourceDebugToolService): AspireResourceDebugToolRegistration {
    const tool = new AspireResourceDebugLanguageModelTool(service);
    const tools = new Map<string, vscode.LanguageModelTool<unknown>>([
        [aspireResourceDebugToolName, tool],
    ]);

    return registerLanguageModelTools(tools, 'resource debug');
}
