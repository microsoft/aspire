import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getTerminalCommandCount, isSamePath, waitForCommandOutcome, waitForExtensionState, waitForRepositoryIdle, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { createAdditionalAppHostCandidate, executeE2eControlCommand, removeAdditionalAppHostCandidate, removeWorkspaceAppHostConfig, restoreE2eCliPathForE2E, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, setCliUnavailableForE2E, setE2eCliPathForE2E, setTerminalCommandExecutionSuppressedForE2E, writeWorkspaceCliPath } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { executeCommandFromPalette, openAspireView, waitForEditorTitle, waitForNotificationMessage, waitForTerminalChannel, waitForWorkbenchText } from './helpers/vscode';

suite('Aspire command palette E2E', function () {
    this.timeout(180000);

    teardown(async () => {
        const failures: unknown[] = [];
        for (const cleanup of [
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
        ]) {
            try {
                await cleanup();
            } catch (error) {
                failures.push(error);
            }
        }

        try {
            restoreWorkspaceAppHostConfig();
        } catch (error) {
            failures.push(error);
        }

        try {
            removeAdditionalAppHostCandidate();
        } catch (error) {
            failures.push(error);
        }

        if (failures.length > 0) {
            throw new AggregateError(failures, 'Command palette E2E teardown failed.');
        }
    });

    test('opens an Aspire terminal through the command palette with the configured CLI path', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const before = getCommandInvocationCount('aspire-vscode.openTerminal');
        await executeCommandFromPalette('Aspire: Open Aspire terminal');
        await waitForCommandOutcome('aspire-vscode.openTerminal', 'success', 60000, before);

        const channel = await waitForTerminalChannel('Aspire');
        assert.ok(channel.includes('Aspire'), `Expected Aspire terminal channel, got '${channel}'.`);
    });

    test('surfaces invalid CLI configuration as a notification and canceled command outcome', async () => {
        const missingCliPath = path.join(getWorkspaceRoot(), 'missing cli folder', process.platform === 'win32' ? 'aspire.cmd' : 'aspire');
        await writeWorkspaceCliPath(missingCliPath);
        await setCliUnavailableForE2E(true);
        const before = getCommandInvocationCount('aspire-vscode.openTerminal');
        await executeCommandFromPalette('Aspire: Open Aspire terminal');
        await waitForNotificationMessage('Aspire CLI is not available');
        await waitForCommandOutcome('aspire-vscode.openTerminal', 'canceled', 60000, before);
    });

    test('routes terminal commands through a configured Windows cmd wrapper path with spaces', async function () {
        if (process.platform !== 'win32') {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const wrapperDirectory = path.join(getWorkspaceRoot(), 'cli wrapper with spaces');
        const wrapperPath = path.join(wrapperDirectory, 'aspire.cmd');
        fs.mkdirSync(wrapperDirectory, { recursive: true });
        fs.writeFileSync(wrapperPath, '@echo off\r\nif "%~1"=="--version" (\r\n  echo 13.5.0-pr.e2e\r\n  exit /b 0\r\n)\r\nexit /b 0\r\n');
        await setE2eCliPathForE2E(undefined);
        await writeWorkspaceCliPath(wrapperPath);
        await setTerminalCommandExecutionSuppressedForE2E(true);

        const beforeInvocation = getCommandInvocationCount('aspire-vscode.new');
        const beforeTerminalCommand = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.new' });
        await waitForCommandOutcome('aspire-vscode.new', 'success', 60000, beforeInvocation);

        const terminalCommand = await waitForTerminalCommand(
            event => event.executionSuppressed && event.subcommand === 'new' && event.commandLine.includes(`& "${wrapperPath}" new`),
            'Windows cmd wrapper terminal routing',
            60000,
            beforeTerminalCommand);
        assert.strictEqual(terminalCommand.executionSuppressed, true);
    });

    test('opens settings UI and writes launch configuration through command palette commands', async () => {
        const settingsBefore = getCommandInvocationCount('aspire-vscode.settings');
        await executeCommandFromPalette('Aspire: Extension settings');
        await waitForCommandOutcome('aspire-vscode.settings', 'success', 60000, settingsBefore);
        await waitForWorkbenchText('Settings');
        await waitForWorkbenchText('Aspire: App Host Discovery Timeout Ms');
        await executeE2eControlCommand({ name: 'closeAllEditors' });

        const configureBefore = getCommandInvocationCount('aspire-vscode.configureLaunchJson');
        await executeCommandFromPalette('Aspire: Configure launch.json file');
        await waitForCommandOutcome('aspire-vscode.configureLaunchJson', 'success', 60000, configureBefore);
        assert.ok((await waitForEditorTitle('launch.json')).includes('launch.json'));

        const launchJsonPath = path.join(getWorkspaceRoot(), '.vscode', 'launch.json');
        const launchJson = JSON.parse(fs.readFileSync(launchJsonPath, 'utf8')) as { configurations?: Array<{ type?: string; request?: string }> };
        assert.ok(launchJson.configurations?.some(configuration => configuration.type === 'aspire' && configuration.request === 'launch'));
    });

    test('observes multiple AppHost candidates without selecting the wrong one', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        removeWorkspaceAppHostConfig();
        const secondaryAppHostPath = createAdditionalAppHostCandidate();
        const beforeRefresh = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, beforeRefresh);

        const stateFile = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, secondaryAppHostPath)),
            'secondary AppHost candidate',
            60000);

        assert.ok(stateFile.state.workspaceAppHostCandidatePaths.length >= 2);
    });
});
