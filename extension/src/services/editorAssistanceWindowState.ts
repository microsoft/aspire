import { resetAppHostIdentityRegistry } from '../utils/appHostIdentity';
import { resetLaunchFailureJournal } from './launchFailureJournal';

/**
 * Clears editor-assistance state whose lifetime is one extension-host activation.
 *
 * VS Code can deactivate and reactivate an extension in the same process. Resetting both
 * stores at activation and final teardown prevents an opaque identity or failure record
 * from being observed in the next extension window.
 */
export function resetEditorAssistanceWindowState(): void {
    resetLaunchFailureJournal();
    resetAppHostIdentityRegistry();
}
