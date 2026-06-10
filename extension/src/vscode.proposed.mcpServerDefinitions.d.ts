/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Type declarations for the vscode proposed API: mcpServerDefinitions
// https://github.com/microsoft/vscode/blob/main/src/vscode-dts/vscode.proposed.mcpServerDefinitions.d.ts

declare module 'vscode' {

    export class McpStdioServerDefinition {
        readonly label: string;
        cwd?: Uri;
        command: string;
        args: string[];
        env: Record<string, string | number | null>;
        version?: string;
        constructor(label: string, command: string, args?: string[], env?: Record<string, string | number | null>, version?: string);
    }

    export class McpHttpServerDefinition {
        readonly label: string;
        uri: Uri;
        headers: Record<string, string>;
        version?: string;
        constructor(label: string, uri: Uri, headers?: Record<string, string>, version?: string);
    }

    export type McpServerDefinition = McpStdioServerDefinition | McpHttpServerDefinition;

    export interface McpServerDefinitionProvider<T extends McpServerDefinition = McpServerDefinition> {
        readonly onDidChangeMcpServerDefinitions?: Event<void>;
        provideMcpServerDefinitions(token: CancellationToken): ProviderResult<T[]>;
        resolveMcpServerDefinition?(server: T, token: CancellationToken): ProviderResult<T>;
    }

    export namespace lm {
        export function registerMcpServerDefinitionProvider(id: string, provider: McpServerDefinitionProvider): Disposable;
    }
}
