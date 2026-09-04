import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, waitForCommandOutcome, waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, reloadWorkspaceForE2E, removePath, restoreWorkspaceAppHostConfig, runE2eTeardown, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { runProcess } from './helpers/process';
import { getCliPath, getPrimaryAppHostProjectPath, getRepoRoot, getWorkspaceRoot } from './helpers/paths';

function delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

/**
 * Polls a file's content until `predicate` returns true, or throws once `timeoutMs` elapses. A
 * missing file (ENOENT, or any other transient read error) is treated as "not yet" rather than a
 * hard failure, mirroring the polling shape of `waitForExtensionState`/`waitForHttpText` in
 * `helpers/assertions.ts`.
 */
async function waitForFileContent(filePath: string, predicate: (content: string) => boolean, description: string, timeoutMs = 120000): Promise<string> {
    const deadline = Date.now() + timeoutMs;
    let lastContent: string | undefined;
    let lastError: Error | undefined;

    while (Date.now() < deadline) {
        try {
            const content = fs.readFileSync(filePath, 'utf8');
            lastContent = content;
            if (predicate(content)) {
                return content;
            }
        }
        catch (error) {
            lastError = error instanceof Error ? error : new Error(String(error));
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}.\nLast content: ${lastContent ?? '<none>'}\nLast error: ${lastError?.message ?? '<none>'}`);
}

/**
 * Polls a file's mtime until it is strictly newer than `afterMs`, or throws once `timeoutMs`
 * elapses. Used to prove a restore rewrote an already-present marker file, since a content check
 * alone can't distinguish "restore ran again" from "restore never ran" once the marker already
 * holds the expected value.
 */
async function waitForFileModifiedAfter(filePath: string, afterMs: number, description: string, timeoutMs = 120000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    let lastMtimeMs: number | undefined;
    let lastError: Error | undefined;

    while (Date.now() < deadline) {
        try {
            const mtimeMs = fs.statSync(filePath).mtimeMs;
            lastMtimeMs = mtimeMs;
            if (mtimeMs > afterMs) {
                return;
            }
        }
        catch (error) {
            lastError = error instanceof Error ? error : new Error(String(error));
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}.\nLast mtimeMs: ${lastMtimeMs ?? '<none>'} (needed > ${afterMs})\nLast error: ${lastError?.message ?? '<none>'}`);
}

suite('Aspire auto-restore E2E', function () {
    this.timeout(300000);

    const guestAppHostDirectory = path.join(getWorkspaceRoot(), 'AutoRestoreAppHost');
    const guestAppHostPath = path.join(guestAppHostDirectory, 'apphost.mts');
    const guestMarkerPath = path.join(guestAppHostDirectory, '.aspire', 'modules', '.codegen-version');
    const staleMarkerVersion = '0.0.0-e2e-stale-marker';

    const primaryAppHostPath = getPrimaryAppHostProjectPath();
    const primaryAppHostDirectory = path.dirname(primaryAppHostPath);
    const primaryModulesDirectory = path.join(primaryAppHostDirectory, '.aspire', 'modules');
    const primaryLegacyModulesDirectory = path.join(primaryAppHostDirectory, '.modules');

    teardown(async () => {
        await runE2eTeardown([
            () => restoreWorkspaceAppHostConfig(),
            () => removePath(guestAppHostDirectory, { recursive: true, force: true }),
        ], 'Auto-restore E2E teardown failed.');
    });

    test('automatically restores a stale non-.NET AppHost on activation and force-restores it via the manual command', async function () {
        this.timeout(600000);

        // A real `aspire init` is the only way to get a guest-language AppHost whose on-disk
        // scaffolding matches what the extension's marker-based staleness check expects (Theme F3
        // added the `.codegen-version` write this flow depends on to the same `init` code path).
        fs.mkdirSync(guestAppHostDirectory, { recursive: true });
        await runProcess(
            getCliPath(),
            ['init', '--language', 'typescript', '--non-interactive', '--suppress-agent-init', '--nologo'],
            {
                cwd: guestAppHostDirectory,
                timeoutMs: 180000,
                // Guest-language code generation runs through the CLI's AppHost server over RPC,
                // which the CLI locates from an installed bundle or, in dev mode, from the Aspire
                // repository root. The E2E harness copies the CLI to a temp location with no
                // bundle, so without this the CLI fails with "No Aspire AppHost server is
                // available" (the same issue the Java E2E environment already works around in
                // run-e2e.js's `getAspireCliEnvironment`).
                env: { ASPIRE_REPO_ROOT: getRepoRoot() },
            });

        assert.ok(fs.existsSync(guestMarkerPath), `Expected 'aspire init' to scaffold a code-generation version marker at '${guestMarkerPath}'.`);

        // Simulate a stale marker (as if the AppHost had been scaffolded by an older CLI) so that
        // reactivating the extension has something it should legitimately restore.
        writeFileWithRetry(guestMarkerPath, staleMarkerVersion);
        writeWorkspaceAppHostConfigForPath(guestAppHostPath);

        // Auto-restore only re-runs on a handful of explicit triggers (configuration changes,
        // workspace folder changes, trust grants, and the one-time activation call) - none of which
        // fire just because `aspire.config.json` changed on disk. Reloading the window re-triggers
        // the activation-time pass deterministically, and doubles as the most realistic simulation
        // of the "reopen VS Code after `aspire init`" scenario this feature targets.
        await reloadWorkspaceForE2E();
        await waitForRepositoryIdle();

        await waitForFileContent(
            guestMarkerPath,
            content => content.trim().length > 0 && content.trim() !== staleMarkerVersion,
            `automatic restore rewriting stale marker at '${guestMarkerPath}'`);

        const markerMtimeBeforeManualRestore = fs.statSync(guestMarkerPath).mtimeMs;
        const beforeRestoreInvocations = getCommandInvocationCount('aspire-vscode.restoreAppHost');

        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.restoreAppHost', args: [{ appHostPath: guestAppHostPath }] });
        await waitForCommandOutcome('aspire-vscode.restoreAppHost', 'success', 60000, beforeRestoreInvocations);

        // The manual restore command sends real CLI invocation text to a visible terminal rather
        // than spawning a directly-trackable child process, so a 'success' command outcome only
        // proves the text was sent - not that the underlying `aspire restore` process has finished.
        // A forced restore always rewrites the marker even though it is already fresh after the
        // automatic pass above, so a newer mtime is the only reliable completion signal available.
        await waitForFileModifiedAfter(
            guestMarkerPath,
            markerMtimeBeforeManualRestore,
            `manual restore rewriting marker at '${guestMarkerPath}'`);
    });

    test('does not create restore artifacts for a .NET AppHost', async function () {
        this.timeout(300000);

        restoreWorkspaceAppHostConfig();

        // Force a fresh activation pass under this test's control rather than relying on the
        // one-time pass from suite startup, which ran before this test - and possibly before other
        // tests mutated the workspace config - so it isn't a reliable, order-independent signal.
        await reloadWorkspaceForE2E();
        await waitForRepositoryIdle();

        // There is no positive signal to wait on for "nothing happened", so give the (expected to
        // be instantaneous, no-op for a .NET AppHost) restore pass a generous fixed window instead.
        await delay(5000);

        assert.ok(!fs.existsSync(primaryModulesDirectory), `Expected no '${primaryModulesDirectory}' to be created for a .NET AppHost.`);
        assert.ok(!fs.existsSync(primaryLegacyModulesDirectory), `Expected no '${primaryLegacyModulesDirectory}' to be created for a .NET AppHost.`);
    });
});
