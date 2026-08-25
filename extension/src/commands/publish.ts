import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function publishCommand(
    editorCommandProvider: AspireEditorCommandProvider,
    appHostPath: string,
    target: CliPathResolutionTarget,
    cliPath: string,
) {
    await editorCommandProvider.tryExecutePublishAppHost(false, appHostPath, target, cliPath);
}
