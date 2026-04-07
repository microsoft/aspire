import { MessageConnection } from 'vscode-jsonrpc';
import { extensionLogOutputChannel, logAsyncOperation } from '../utils/logging';
import { IInteractionService, InteractionService } from './interactionService';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AspireDebugSession } from '../debugger/AspireDebugSession';

export interface ICliRpcClient {
    debugSessionId: string | null;
    interactionService: IInteractionService;
    getCliVersion(): Promise<string>;
    validatePromptInputString(input: string): Promise<ValidationResult | null>;
    stopCli(): Promise<void>;
    getCliCapabilities(): Promise<string[]>;
}

export type ValidationResult = {
    Message: string;
    Successful: boolean;
};

export class RpcClient implements ICliRpcClient {
    private _messageConnection: MessageConnection;
    private _connectionClosed: boolean;
    private _terminalProvider: AspireTerminalProvider;

    public debugSessionId: string | null;
    public interactionService: IInteractionService;

    constructor(terminalProvider: AspireTerminalProvider, messageConnection: MessageConnection, debugSessionId: string | null, getAspireDebugSession: () => AspireDebugSession | null) {
        this._terminalProvider = terminalProvider;
        this._messageConnection = messageConnection;
        this._connectionClosed = false;
        this.debugSessionId = debugSessionId;
        this.interactionService = new InteractionService(getAspireDebugSession, this, () => terminalProvider.getAspireTerminal());

        this._messageConnection.onClose(() => {
            this._connectionClosed = true;
            extensionLogOutputChannel.info('JSON-RPC connection closed');
        });
    }

    getCliVersion(): Promise<string> {
        return logAsyncOperation(
            `Requesting CLI version from CLI`,
            (version: string) => `Received CLI version: ${version}`,
            async () => {
                return await this._messageConnection.sendRequest<string>('getCliVersion');
            }
        );
    }

    validatePromptInputString(input: string): Promise<ValidationResult | null> {
        return logAsyncOperation(
            `Validating prompt input string`,
            (result: ValidationResult | null) => `Received validation result: ${JSON.stringify(result)}`,
            async () => {
                return await this._messageConnection.sendRequest<ValidationResult | null>('validatePromptInputString', {
                    input
                });
            }
        );
    }

    async stopCli() {
        if (!this._connectionClosed) {
            await this._messageConnection.sendRequest('stopCli');
        }
    }

    async getCliCapabilities(): Promise<string[]> {
        try {
            return await logAsyncOperation(
                `Requesting CLI capabilities from CLI`,
                (capabilities: string[]) => `Received CLI capabilities: ${capabilities.join(', ')}`,
                async () => {
                    return await this._messageConnection.sendRequest<string[]>('getCliCapabilities');
                }
            );
        } catch (error) {
            // Old CLI versions don't support getCliCapabilities, return empty array
            extensionLogOutputChannel.info(`CLI does not support getCliCapabilities (error: ${error}), assuming no capabilities`);
            return [];
        }
    }
}
