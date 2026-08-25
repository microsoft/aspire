import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AppHostCommandTarget } from '../utils/appHostArgs';
import { CliPathResolutionTarget, windowCliPathTarget } from '../utils/cliPathVariables';

export async function updateCommand(
    terminalProvider: AspireTerminalProvider,
    _editorCommandProvider: AspireEditorCommandProvider,
    appHost: AppHostCommandTarget,
    target: CliPathResolutionTarget,
    cliPath: string,
) {
    await terminalProvider.sendAspireCommandToAspireTerminal('update', true, appHost.args, { target, cliPath });
}

export async function updateSelfCommand(
    terminalProvider: AspireTerminalProvider,
    target: CliPathResolutionTarget = windowCliPathTarget,
    cliPath?: string,
) {
    const options = cliPath ? { target, cliPath } : { target };
    await terminalProvider.sendAspireCommandToAspireTerminal('update --self', true, undefined, options);
}
