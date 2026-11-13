import * as path from 'path';
import { waitForRepositoryIdle, waitForWorkspaceAppHostCandidate } from './assertions';
import { executeE2eControlCommand } from './fixtures';
import { getWorkspaceRoot } from './paths';
import { openAspireView } from './vscode';

export const JAVA_APP_HOST_DIRECTORY = 'JavaSpringBoot.AppHost.Java';
export const JAVA_APP_HOST_SOURCE = path.join(JAVA_APP_HOST_DIRECTORY, 'AppHost.java');
export const JAVA_STARTER_APP_HOST_SOURCE = 'AppHost.java';

export function getJavaAppHostSourcePath(): string {
    return path.join(getWorkspaceRoot(), JAVA_APP_HOST_SOURCE);
}

export function getJavaStarterAppHostSourcePath(): string {
    return path.join(getWorkspaceRoot(), JAVA_STARTER_APP_HOST_SOURCE);
}

/**
 * Brings the window to the state every Java spec needs, and fails loudly when it cannot.
 *
 * The Aspire extension only activates on a view command or a workspace match, and the E2E state
 * file does not exist until it has, so every spec has to open the view before it reads anything.
 * The capability assertion then covers the other half: the specs are meaningless without the Java
 * extensions, and every symptom of their absence is a timeout minutes later that names something
 * unrelated.
 */
export async function prepareJavaWorkspace(): Promise<void> {
    await openAspireView();
    await assertJavaCapabilityAdvertised();
    await waitForJavaAppHostCandidate();
    await waitForRepositoryIdle();
}

export async function prepareJavaStarterWorkspace(): Promise<void> {
    await openAspireView();
    await assertJavaCapabilityAdvertised();
    await waitForWorkspaceAppHostCandidate(getJavaStarterAppHostSourcePath());
    await waitForRepositoryIdle();
}

/**
 * Waits for discovery to surface the single-file Java AppHost.
 *
 * The shared no-argument helper looks for the scaffolded C# fixture, which the Java run removes from
 * the workspace, so this matches on `AppHost.java` instead.
 */
export async function waitForJavaAppHostCandidate(timeoutMs?: number): Promise<void> {
    await waitForWorkspaceAppHostCandidate(getJavaAppHostSourcePath(), timeoutMs);
}

/**
 * Fails unless the extension advertises the `java` capability to the CLI.
 *
 * The CLI only asks the extension to launch a Java AppHost when this capability is present, and it
 * silently spawns `java` itself when it is not, so an AppHost still starts and no breakpoint ever
 * binds. The capability requires both redhat.java and vscjava.vscode-java-debug to be installed,
 * which is the single thing most likely to be wrong about a Java E2E run.
 */
export async function assertJavaCapabilityAdvertised(): Promise<void> {
    const capabilities = (await executeE2eControlCommand({ name: 'getSupportedCapabilities' })).result as string[];
    if (!capabilities.includes('java')) {
        // The runner already verified the extensions directory and extensions.json, so when the
        // capability is still missing the useful question is what the extension host can see: a copied
        // extension directory is only scanned while extensions.json is absent, which leaves both of the
        // runner's checks passing while the host loads nothing.
        const visibleExtensionIds = (await executeE2eControlCommand({ name: 'getVisibleExtensionIds' })).result as string[];
        throw new Error(`The extension is not advertising the 'java' capability, so the CLI will not ask it to launch a Java AppHost. Install redhat.java and vscjava.vscode-java-debug into the E2E instance. Advertised capabilities: ${capabilities.join(', ')}. Extensions visible to the extension host: ${visibleExtensionIds.join(', ')}`);
    }
}

/**
 * Waits until redhat.java has imported the workspace.
 *
 * Diagnostics are empty both before the language server has looked at a file and after it has
 * declared that file clean, so anything asserting on them has to wait for the import first or it is
 * only asserting that nothing analysed the workspace.
 */
export async function waitForJavaLanguageServerImport(timeoutMs = 900000): Promise<void> {
    await executeE2eControlCommand({ name: 'waitForJavaLanguageServer', timeoutMs }, { timeoutMs: timeoutMs + 60000 });
}
