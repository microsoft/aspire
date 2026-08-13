import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { isSamePath, readStateFile, waitForExtensionState, waitForNoDebugSessions, waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceFoldersForE2E, runE2eTeardown, setWorkspaceFoldersForE2E } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { chooseActiveQuickPick, executeCommandFromPalette, getActiveQuickPickLabels, openAspireView, waitForEditorTitle } from './helpers/vscode';

suite('Aspire dynamic debug configuration E2E', function () {
    this.timeout(240000);

    const fixtureRoot = path.join(getWorkspaceRoot(), '.e2e-dynamic-debug');
    const firstFolderPath = path.join(fixtureRoot, 'first');
    const secondFolderPath = path.join(fixtureRoot, 'second');
    const appHostPath = path.join(secondFolderPath, 'apphost.cs');

    teardown(async () => {
        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => restoreWorkspaceFoldersForE2E(),
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => fs.rmSync(fixtureRoot, { recursive: true, force: true }),
        ], 'Dynamic debug configuration E2E teardown failed.');
    });

    test('restores the selected AppHost when duplicate workspace folder aliases are debugged again with F5', async () => {
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

        const configurationLabels = quickPickLabels.filter(label => label.trimStart().startsWith('Aspire: Launch default AppHost'));
        assert.strictEqual(configurationLabels.length, 2, `Expected two Aspire dynamic configurations. Visible labels: ${JSON.stringify(quickPickLabels)}`);
        assert.notStrictEqual(configurationLabels[0], configurationLabels[1]);

        const secondFolderConfiguration = configurationLabels.find(label => label.includes('/second'));
        assert.ok(secondFolderConfiguration);
        const beforeFirstLaunch = getDebugConsoleOutputCount();
        await chooseActiveQuickPick(secondFolderConfiguration);

        const firstLaunch = await waitForLaunchOutput(beforeFirstLaunch);
        assert.ok(firstLaunch.appHostPath);
        assert.ok(isSamePath(firstLaunch.appHostPath, appHostPath));
        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();

        await executeE2eControlCommand({ name: 'openFile', filePath: appHostPath });
        await waitForEditorTitle('apphost.cs');
        const beforeSecondLaunch = getDebugConsoleOutputCount();
        // F5 invokes this command. Using the palette keeps the browser-based test runner from
        // consuming F5 as a page reload before VS Code receives it.
        await executeCommandFromPalette('Debug: Start Debugging');
        const secondLaunch = await waitForLaunchOutput(beforeSecondLaunch);
        assert.ok(secondLaunch.appHostPath);
        assert.ok(isSamePath(secondLaunch.appHostPath, appHostPath));
    });

    function createWorkspaceFixture(): void {
        fs.rmSync(fixtureRoot, { recursive: true, force: true });
        fs.mkdirSync(firstFolderPath, { recursive: true });
        fs.mkdirSync(secondFolderPath, { recursive: true });
        const appHostSdkVersion = process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
        assert.ok(appHostSdkVersion);
        fs.writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@${appHostSdkVersion}

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`);
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
