import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { resolvePipelineStep } from '../utils/pipelineStep';

export async function doCommand(terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider) {
    const step = await resolvePipelineStep(terminalProvider);
    if (step === undefined) {
        throw new vscode.CancellationError();
    }
    await editorCommandProvider.tryExecuteDoAppHost(false, step ?? undefined);
}
