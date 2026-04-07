import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getAppHostArgs } from '../utils/appHostArgs';

export async function updateCommand(terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider) {
    const appHostArgs = await getAppHostArgs(editorCommandProvider);
    await terminalProvider.sendAspireCommandToAspireTerminal('update', true, appHostArgs);
}

export async function updateSelfCommand(terminalProvider: AspireTerminalProvider) {
    await terminalProvider.sendAspireCommandToAspireTerminal('update --self');
}
