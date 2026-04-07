import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getAppHostArgs } from '../utils/appHostArgs';

export async function addCommand(terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider) {
    const appHostArgs = await getAppHostArgs(editorCommandProvider);
    await terminalProvider.sendAspireCommandToAspireTerminal('add', true, appHostArgs);
}
