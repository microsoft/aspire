import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function deployCommand(
    editorCommandProvider: AspireEditorCommandProvider,
    appHostPath: string,
    target: CliPathResolutionTarget,
    cliPath: string,
) {
    await editorCommandProvider.tryExecuteDeployAppHost(false, appHostPath, target, cliPath);
}
