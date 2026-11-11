import * as vscode from 'vscode';

import { extensionLogOutputChannel } from '../utils/logging';

export interface LanguageModelToolRegistration extends vscode.Disposable {
    readonly registered: boolean;
    /**
     * The registered tool instances by tool name. VS Code does not surface
     * `prepareInvocation` through `vscode.lm`, so E2E automation needs a way to ask the
     * extension's own instance for preparation and pre-cancelled invocation.
     */
    readonly tools: ReadonlyMap<string, vscode.LanguageModelTool<unknown>>;
}

export function registerLanguageModelTools(
    tools: ReadonlyMap<string, vscode.LanguageModelTool<unknown>>,
    groupName: string): LanguageModelToolRegistration {
    const registrations: vscode.Disposable[] = [];
    if (typeof vscode.lm?.registerTool !== 'function') {
        extensionLogOutputChannel.info(`Skipping Aspire ${groupName} language model tools: the language model tool API is unavailable.`);
    }
    else {
        for (const [name, tool] of tools) {
            registrations.push(vscode.lm.registerTool(name, tool));
        }
        extensionLogOutputChannel.info(`Registered Aspire ${groupName} language model tools.`);
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
