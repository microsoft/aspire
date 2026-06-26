import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import type { AppHostLaunchOptions } from '../services/AppHostLaunchService';

export async function deployCommand(editorCommandProvider: AspireEditorCommandProvider, launchOptions?: AppHostLaunchOptions) {
    await editorCommandProvider.tryExecuteDeployAppHost(false, launchOptions);
}
