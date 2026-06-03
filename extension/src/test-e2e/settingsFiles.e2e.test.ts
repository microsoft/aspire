import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, setCliUnavailableForE2E } from './helpers/fixtures';
import { getCliPath, getWorkspaceRoot } from './helpers/paths';
import { closeAllEditors, openAspireView, waitForEditorTitle } from './helpers/vscode';

interface ActiveEditorInfo {
    fileName?: string;
}

suite('Aspire settings file command E2E', function () {
    this.timeout(180000);

    teardown(async () => {
        await setCliUnavailableForE2E(false);
        await restoreWorkspaceCliPath();
    });

    test('creates and opens local and isolated global Aspire settings files', async () => {
        await openAspireView();
        await waitForWorkspaceAppHost();
        await waitForRepositoryIdle();

        assertNoInstallRouteSidecar();

        const localSettingsPath = path.join(getWorkspaceRoot(), 'aspire.config.json');
        let before = getCommandInvocationCount('aspire-vscode.openLocalSettings');
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.openLocalSettings' });
        await waitForCommandOutcome('aspire-vscode.openLocalSettings', 'success', 60000, before);
        await waitForEditorTitle('aspire.config.json');
        await waitForActiveEditorPath(localSettingsPath);
        assert.ok(fs.existsSync(localSettingsPath), `Expected local settings file at ${localSettingsPath}.`);

        const aspireHome = process.env.ASPIRE_HOME;
        assert.ok(aspireHome, 'ASPIRE_HOME must be isolated by the E2E runner before testing global settings commands.');
        assert.ok(!isSamePath(aspireHome, path.join(os.homedir(), '.aspire')), `ASPIRE_HOME must not point at the real user profile: ${aspireHome}`);

        const globalSettingsPath = path.join(aspireHome, 'aspire.config.json');
        fs.rmSync(globalSettingsPath, { force: true });
        await closeAllEditors();

        before = getCommandInvocationCount('aspire-vscode.openGlobalSettings');
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.openGlobalSettings' });
        await waitForCommandOutcome('aspire-vscode.openGlobalSettings', 'success', 60000, before);
        await waitForEditorTitle('aspire.config.json');
        await waitForActiveEditorPath(globalSettingsPath);
        assert.strictEqual(fs.readFileSync(globalSettingsPath, 'utf8'), '{}');
    });
});

async function waitForActiveEditorPath(expectedPath: string, timeoutMs = 60000): Promise<ActiveEditorInfo> {
    const started = Date.now();
    let lastEditor: ActiveEditorInfo | undefined;
    while (Date.now() - started < timeoutMs) {
        const result = (await executeE2eControlCommand({ name: 'getActiveEditor' })).result as ActiveEditorInfo;
        lastEditor = result;
        if (result.fileName && isSamePath(result.fileName, expectedPath)) {
            return result;
        }

        await delay(200);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for active editor '${expectedPath}'. Last active editor: ${JSON.stringify(lastEditor)}`);
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function assertNoInstallRouteSidecar(): void {
    const sidecarPath = path.join(path.dirname(getCliPath()), '.aspire-install.json');
    assert.ok(!fs.existsSync(sidecarPath), `The E2E runner must use an isolated CLI copy without ${path.basename(sidecarPath)} so ASPIRE_HOME controls global settings paths. Found: ${sidecarPath}`);
}
