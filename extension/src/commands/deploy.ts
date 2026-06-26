import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

export async function deployCommand(editorCommandProvider: AspireEditorCommandProvider) {
    await editorCommandProvider.tryExecuteDeployAppHost(false);
}
