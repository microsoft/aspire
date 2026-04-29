import * as vscode from 'vscode';
import { aspireCliLogsChannelName, aspireOutputChannelName } from '../loc/strings';

export const extensionLogOutputChannel: vscode.LogOutputChannel = vscode.window.createOutputChannel(aspireOutputChannelName, { log: true });
export const cliLogsOutputChannel: vscode.OutputChannel = vscode.window.createOutputChannel(aspireCliLogsChannelName);

export async function logAsyncOperation<T>(beforeMessage: string, afterMessage: (result: T) => string, operation: () => Promise<T>): Promise<T> {
    extensionLogOutputChannel.info( beforeMessage);
    try {
        const result = await operation();
        extensionLogOutputChannel.info(afterMessage(result));
        return result;
    } catch (error) {
        const errorMessage = error instanceof Error ? error.message : JSON.stringify(error);
        extensionLogOutputChannel.error(`Error during operation: ${errorMessage}`);
        throw error;
    }
}
