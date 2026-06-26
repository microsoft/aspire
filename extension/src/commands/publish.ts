import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import type { AppHostLaunchOptions } from '../services/AppHostLaunchService';

export async function publishCommand(editorCommandProvider: AspireEditorCommandProvider, launchOptions?: AppHostLaunchOptions) {
    await editorCommandProvider.tryExecutePublishAppHost(false, launchOptions);
}
