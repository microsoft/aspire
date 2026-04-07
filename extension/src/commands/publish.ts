import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

export async function publishCommand(editorCommandProvider: AspireEditorCommandProvider) {
    await editorCommandProvider.tryExecutePublishAppHost(false);
}
