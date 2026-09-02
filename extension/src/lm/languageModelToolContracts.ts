import type * as vscode from 'vscode';

/**
 * The limited preparation surface exposed to the E2E bridge. It intentionally accepts
 * raw JSON-shaped input because each registered tool independently validates every field.
 */
export interface PreparableLanguageModelTool {
    prepareInvocation(
        options: { readonly input: Record<string, unknown> },
        token: vscode.CancellationToken,
    ): Promise<vscode.PreparedToolInvocation>;
}

export interface PreparableLanguageModelToolRegistration extends vscode.Disposable {
    readonly registered: boolean;
    readonly tools: ReadonlyMap<string, PreparableLanguageModelTool>;
}
