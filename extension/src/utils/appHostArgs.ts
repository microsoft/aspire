import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

/**
 * Returns CLI arguments to pass the resolved AppHost project path via --apphost,
 * or undefined if no AppHost is currently available.
 */
export async function getAppHostArgs(editorCommandProvider: AspireEditorCommandProvider): Promise<string[] | undefined> {
    const appHostPath = await editorCommandProvider.getAppHostPath();
    if (!appHostPath) {
        return undefined;
    }

    return ['--apphost', appHostPath];
}
