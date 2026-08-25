import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getTerminalCommandCount, waitForCommandOutcome, waitForRepositoryIdle, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import {
    executeE2eControlCommand,
    getCliWrapperInvocationCount,
    restoreE2eCliPathForE2E,
    restoreWorkspaceCliPath,
    runE2eTeardown,
    setE2eCliPathForE2E,
    setTerminalCommandExecutionSuppressedForE2E,
    touchPrimaryAppHostProject,
    waitForCliWrapperInvocation,
    writeOutdatedCliWarningWrapper,
    writeTrackedStreamingDiscoveryCliWrapper,
    writeWorkspaceCliPath,
} from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { VSBrowser } from './helpers/extester';
import { executeCommandFromPalette, openAspireView, waitForNotificationMessage, waitForWorkbenchText } from './helpers/vscode';

// Mirrors configuredCliPathRejected in src/loc/strings.ts.
const rejectionNotificationText = 'The configured Aspire CLI path could not be used';
const openSettingActionText = 'Open Setting';
const outdatedCliWarningText = 'Aspire CLI 13.4.9 is older than 13.5.0';
const updateCliActionText = 'Update Aspire CLI';

suite('Configured CLI path rejection E2E', function () {
    this.timeout(300000);

    let cleanupOutdatedCliWrapper: (() => void) | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => executeCommandFromPalette('workbench.view.explorer'),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
            () => cleanupOutdatedCliWrapper?.(),
        ], 'Configured CLI path rejection E2E teardown failed.');
        cleanupOutdatedCliWrapper = undefined;
    });

    test('resolves a configured directory that contains the aspire executable', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const wrapper = writeTrackedStreamingDiscoveryCliWrapper(0, 0);

        // Simulates pointing the setting at a build output folder such as
        // artifacts/bin/Aspire.Cli/Debug/net10.0, which contains an `aspire` executable.
        const cliDirectory = path.join(getWorkspaceRoot(), '.e2e-cli-wrappers', 'build-output-directory');
        fs.rmSync(cliDirectory, { recursive: true, force: true });
        fs.mkdirSync(cliDirectory, { recursive: true });
        const executableInDirectory = path.join(cliDirectory, process.platform === 'win32' ? 'aspire.cmd' : 'aspire');
        if (process.platform === 'win32') {
            fs.writeFileSync(executableInDirectory, `@echo off\r\ncall "${wrapper.cliPath}" %*\r\n`);
        }
        else {
            fs.writeFileSync(executableInDirectory, `#!/usr/bin/env sh\nexec ${JSON.stringify(wrapper.cliPath)} "$@"\n`);
            fs.chmodSync(executableInDirectory, 0o755);
        }

        await setE2eCliPathForE2E(undefined);
        await writeWorkspaceCliPath(cliDirectory);

        touchPrimaryAppHostProject();
        await waitForCliWrapperInvocation(wrapper.invocationLogPath, 60_000);
        await VSBrowser.instance.takeScreenshot('cli-path-directory-resolved').catch(() => undefined);

        assert.ok(
            getCliWrapperInvocationCount(wrapper.invocationLogPath) > 0,
            'The CLI inside the configured directory was never invoked.');
    });

    test('warns and opens the setting when the configured path is invalid', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const invalidCliPath = path.join(getWorkspaceRoot(), '.e2e-cli-wrappers', 'not-an-aspire-cli');
        fs.rmSync(invalidCliPath, { recursive: true, force: true });

        await setE2eCliPathForE2E(undefined);
        await writeWorkspaceCliPath(invalidCliPath);
        touchPrimaryAppHostProject();

        const notification = await waitForNotificationMessage(rejectionNotificationText, 90_000);
        const message = await notification.getMessage();
        await VSBrowser.instance.takeScreenshot('cli-path-rejection-notification').catch(() => undefined);

        assert.ok(
            message.includes(invalidCliPath),
            `The rejection notification did not name the configured path. Message: ${message}`);

        await notification.takeAction(openSettingActionText);
        // The Settings editor renders as a webview-backed tab; assert on the visible workbench text
        // so the check does not depend on how the tab title is localized.
        const settingsText = await waitForWorkbenchText('aspireCliExecutablePath', 60_000);
        await VSBrowser.instance.takeScreenshot('cli-path-rejection-open-setting').catch(() => undefined);

        assert.ok(settingsText.length > 0, 'The Aspire CLI executable path setting was not shown.');
    });

    test('warns when active Aspire use resolves an outdated CLI and routes the update action', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await executeCommandFromPalette('workbench.view.explorer');
        const wrapper = writeOutdatedCliWarningWrapper();
        cleanupOutdatedCliWrapper = wrapper.cleanup;
        await setE2eCliPathForE2E(wrapper.cliPath);
        await setTerminalCommandExecutionSuppressedForE2E(true);

        await openAspireView();
        const beforeOpenTerminal = getCommandInvocationCount('aspire-vscode.openTerminal');
        await executeE2eControlCommand({
            name: 'executeAspireCommand',
            commandId: 'aspire-vscode.openTerminal',
        });
        await waitForCommandOutcome('aspire-vscode.openTerminal', 'success', 60_000, beforeOpenTerminal);

        const notification = await waitForNotificationMessage(outdatedCliWarningText, 60_000);
        const message = await notification.getMessage();
        await VSBrowser.instance.takeScreenshot('outdated-aspire-cli-warning').catch(() => undefined);
        assert.ok(message.includes(outdatedCliWarningText), `Unexpected warning message: ${message}`);

        const beforeTerminalCommand = getTerminalCommandCount();
        await notification.takeAction(updateCliActionText);
        const terminalCommand = await waitForTerminalCommand(
            event => event.executionSuppressed && event.subcommand === 'update --self',
            'outdated CLI update action',
            60_000,
            beforeTerminalCommand);

        assert.strictEqual(terminalCommand.executionSuppressed, true);
    });
});
