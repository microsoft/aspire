import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { isSamePath, readStateFile, waitForExtensionState, waitForNoDebugSessions, waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, getRunningAppHostPid, removePath, restoreWorkspaceFoldersForE2E, runE2eTeardown, setWorkspaceFoldersForE2E, stopAppHostIfRunning, waitForKnownProcessExit } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { chooseActiveQuickPick, chooseActiveQuickPickAtIndex, executeCommandFromPalette, getActiveQuickPickLabels, openAspireView, waitForEditorTitle } from './helpers/vscode';

suite('Aspire dynamic debug configuration E2E', function () {
    this.timeout(240000);

    const fixtureRoot = path.join(getWorkspaceRoot(), '.e2e-dynamic-debug');
    const firstFolderPath = path.join(fixtureRoot, 'first');
    const secondFolderPath = path.join(fixtureRoot, 'second');
    const firstAppHostPath = path.join(firstFolderPath, 'apphost.cs');
    const appHostPath = path.join(secondFolderPath, 'apphost.cs');
    let appHostPidBeforeStop: number | undefined;

    setup(() => {
        appHostPidBeforeStop = undefined;
    });

    teardown(async () => {
        await runE2eTeardown([
            () => appHostPidBeforeStop ??= getRunningAppHostPid(appHostPath),
            () => appHostPidBeforeStop ??= getRunningAppHostPid(firstAppHostPath),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopAppHostIfRunning(appHostPath),
            () => stopAppHostIfRunning(firstAppHostPath),
            () => appHostPidBeforeStop === undefined
                ? undefined
                : waitForKnownProcessExit(appHostPidBeforeStop, 'the dynamic debug configuration AppHost process', 30000),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => restoreWorkspaceFoldersForE2E(),
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => removePath(fixtureRoot, { recursive: true, force: true }),
        ], 'Dynamic debug configuration E2E teardown failed.');
    });

    test('re-resolves the selected AppHost by dynamic configuration name in duplicate-alias workspaces', async () => {
        createWorkspaceFixture();
        await openAspireView();
        const workspaceFolders = await setWorkspaceFoldersForE2E([
            { folderPath: firstFolderPath, name: 'src' },
            { folderPath: secondFolderPath, name: 'src' },
        ]);
        assert.deepStrictEqual(workspaceFolders.map(folder => folder.name), ['src', 'src']);

        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'openFile', filePath: appHostPath });
        await waitForEditorTitle('apphost.cs');

        await executeCommandFromPalette('Debug: Select and Start Debugging');
        let quickPickLabels = await getActiveQuickPickLabels();
        const aspireDebugger = quickPickLabels.find(label =>
            label.trimStart().startsWith('Aspire') && !label.trimStart().startsWith('Aspire: Launch default AppHost'));
        if (aspireDebugger) {
            await chooseActiveQuickPick(aspireDebugger);
            quickPickLabels = await waitForQuickPickLabels('Aspire: Launch default AppHost');
        }

        const configurationIndexes = quickPickLabels
            .map((label, index) => label.trimStart().startsWith('Aspire: Launch default AppHost') ? index : -1)
            .filter(index => index >= 0);
        assert.strictEqual(configurationIndexes.length, 2, `Expected two Aspire dynamic configurations. Visible labels: ${JSON.stringify(quickPickLabels)}`);

        const secondFolderConfigurationIndex = configurationIndexes[1];
        const secondFolderConfiguration = quickPickLabels[secondFolderConfigurationIndex];
        assert.ok(
            secondFolderConfiguration.includes(workspaceFolders[1].uri),
            `Expected the selected dynamic configuration to identify the second workspace folder URI '${workspaceFolders[1].uri}'. Selected label: ${JSON.stringify(secondFolderConfiguration)}`);
        const beforeFirstLaunch = getDebugConsoleOutputCount();
        await chooseActiveQuickPickAtIndex(secondFolderConfigurationIndex);

        const firstLaunch = await waitForLaunchOutput(beforeFirstLaunch);
        assert.ok(firstLaunch.appHostPath);
        assert.ok(isSamePath(firstLaunch.appHostPath, appHostPath));
        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();

        const beforeSecondLaunch = getDebugConsoleOutputCount();
        await executeCommandFromPalette('Debug: Start Debugging');

        const secondLaunch = await waitForLaunchOutput(beforeSecondLaunch);
        assert.ok(secondLaunch.appHostPath);
        assert.ok(isSamePath(secondLaunch.appHostPath, appHostPath));
    });

    function createWorkspaceFixture(): void {
        removePath(fixtureRoot, { recursive: true, force: true });
        fs.mkdirSync(firstFolderPath, { recursive: true });
        fs.mkdirSync(secondFolderPath, { recursive: true });
        const appHostSdkVersion = process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
        assert.ok(appHostSdkVersion);
        const appHostSource = `#:sdk Aspire.AppHost.Sdk@${appHostSdkVersion}

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`;
        fs.writeFileSync(firstAppHostPath, appHostSource);
        fs.writeFileSync(appHostPath, appHostSource);
    }
});

function getDebugConsoleOutputCount(): number {
    return Math.max(0, ...readStateFile().debugConsoleOutputs.map(event => event.sequence));
}

async function waitForLaunchOutput(afterOutputSequence: number) {
    const file = await waitForExtensionState(
        stateFile => stateFile.debugConsoleOutputs.some(event =>
            event.sequence > afterOutputSequence &&
            event.appHostPath !== undefined),
        'dynamic debug configuration launch output',
        60000);
    const event = file.debugConsoleOutputs.find(candidate =>
        candidate.sequence > afterOutputSequence &&
        candidate.appHostPath !== undefined);
    assert.ok(event);

    return event;
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitForQuickPickLabels(prefix: string, timeoutMs = 30000): Promise<string[]> {
    const started = Date.now();
    let labels: string[] = [];
    while (Date.now() - started < timeoutMs) {
        labels = await getActiveQuickPickLabels();
        if (labels.some(label => label.trimStart().startsWith(prefix))) {
            return labels;
        }

        await delay(100);
    }

    throw new Error(`Timed out waiting for a quick pick starting with '${prefix}'. Visible labels: ${JSON.stringify(labels)}`);
}
