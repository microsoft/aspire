import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

export interface AppHostCommandTarget {
    readonly appHostPath?: string;
    readonly args?: string[];
}

/**
 * Resolves the AppHost once and returns both its path and CLI arguments.
 */
export async function getAppHostArgs(editorCommandProvider: AspireEditorCommandProvider): Promise<AppHostCommandTarget> {
    const appHostPath = await editorCommandProvider.getAppHostPath();
    if (!appHostPath) {
        return {};
    }

    return { appHostPath, args: ['--apphost', appHostPath] };
}
