import * as assert from 'assert';
import * as path from 'path';
import { findResource, getCommandInvocationCount, getTerminalCommandCount, isSamePath, waitForAppHostLaunching, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForHttpText, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForResourceState, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setTerminalCommandExecutionSuppressedForE2E, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { answerActiveInput, chooseActiveQuickPick, getActiveQuickPickLabels, openAspireView, waitForChildTreeItem, waitForEditorTitle, waitForTreeItem } from './helpers/vscode';

suite('Aspire tree action command E2E', function () {
    this.timeout(300000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoRunningAppHost(),
        ], 'Tree action E2E teardown failed.');
    });

    test('routes view, copy, endpoint, log, and resource commands through tree handlers', async () => {
        let section = await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        let before = getCommandInvocationCount('aspire-vscode.switchToGlobalView');
        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        await waitForCommandOutcome('aspire-vscode.switchToGlobalView', 'success', 60000, before);
        await waitForExtensionState(file => file.state.viewMode === 'global', 'global AppHost view');

        before = getCommandInvocationCount('aspire-vscode.globalRefreshAppHosts');
        await executeE2eControlCommand({ name: 'globalRefreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.globalRefreshAppHosts', 'success', 60000, before);

        before = getCommandInvocationCount('aspire-vscode.switchToWorkspaceView');
        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });
        await waitForCommandOutcome('aspire-vscode.switchToWorkspaceView', 'success', 60000, before);
        await waitForExtensionState(file => file.state.viewMode === 'workspace', 'workspace AppHost view');

        before = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForAppHostLaunching(appHostPath);
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, before);
        await waitForRunningAppHost();
        const runningResources = await waitForResourceState('e2e-worker', ['Running'], 180000);
        const workerResource = findResource(runningResources.state, 'e2e-worker');
        assert.ok(workerResource, 'Expected e2e-worker to be present after AppHost startup.');
        const workerResourceName = workerResource.name;
        await waitForDashboardUrl();

        const commandState = await waitForResource('e2e-worker');
        const commands = findResource(commandState.state, 'e2e-worker')?.commands;
        assert.ok(commands, 'Expected e2e-worker commands in the E2E state snapshot.');
        assert.ok(commands['echo-arguments'], 'Expected enabled echo-arguments command.');
        assert.strictEqual(commands['disabled-e2e-command']?.state, 'Disabled');
        assert.strictEqual(commands['hidden-e2e-command'], undefined);
        assert.strictEqual(commands['api-only-e2e-command']?.visibility, 'Api');
        assert.strictEqual(commands['unknown-state-e2e-command'], undefined);

        section = await openAspireView();
        const workerTreeItem = await waitForTreeItem(section, 'e2e-worker', 60000);
        await workerTreeItem.expand();
        const commandsGroup = await waitForChildTreeItem(workerTreeItem, 'Commands', 60000);
        await commandsGroup.expand();
        const enabledCommandItem = await waitForTreeItem(section, 'echo-arguments', 60000);
        assert.ok(await waitForTreeItem(section, 'disabled-e2e-command', 60000), 'Expected disabled command tree item.');
        assert.strictEqual(await commandsGroup.findChildItem('hidden-e2e-command'), undefined);
        assert.strictEqual(await commandsGroup.findChildItem('api-only-e2e-command'), undefined);
        assert.strictEqual(await commandsGroup.findChildItem('unknown-state-e2e-command'), undefined);

        const noCommandsResource = await waitForTreeItem(section, 'e2e-no-commands', 60000);
        await noCommandsResource.expand();
        assert.strictEqual(await noCommandsResource.findChildItem('Commands'), undefined);

        const copiedAppHost = await executeE2eControlCommand({ name: 'copyAppHostPath', appHostPath });
        assert.ok(isSamePath(String(copiedAppHost.result), appHostPath));

        const openedSource = await executeE2eControlCommand({ name: 'openAppHostSource', appHostPath });
        assert.ok(String((openedSource.result as { fileName?: string }).fileName).endsWith(path.join('AspireE2E.AppHost', 'AppHost.cs')));

        const viewedSource = await executeE2eControlCommand({ name: 'viewAppHostSource', appHostPath });
        assert.ok(String((viewedSource.result as { uri?: string }).uri).startsWith('aspire-source:'));

        const copiedResourceName = await executeE2eControlCommand({ name: 'copyResourceName', appHostPath, resourceName: 'e2e-worker' });
        assert.strictEqual(copiedResourceName.result, 'e2e-worker');

        const copiedEndpointUrl = await executeE2eControlCommand({ name: 'copyEndpointUrl', appHostPath, resourceName: 'e2e-worker' });
        const endpointUrl = String(copiedEndpointUrl.result);
        assert.ok(endpointUrl.startsWith('http'));

        before = getCommandInvocationCount('aspire-vscode.openInIntegratedBrowser');
        await executeE2eControlCommand({ name: 'openInIntegratedBrowser', appHostPath, resourceName: 'e2e-worker' });
        await waitForCommandOutcome('aspire-vscode.openInIntegratedBrowser', 'success', 60000, before);
        assert.ok((await waitForEditorTitle(new URL(endpointUrl).host, 120000, { matchCase: false })).toLowerCase().includes(new URL(endpointUrl).host.toLowerCase()));
        assert.strictEqual(await waitForHttpText(endpointUrl, 'ok'), 'ok');

        const viewedLog = await executeE2eControlCommand({ name: 'viewAppHostLogFile', appHostPath });
        const viewedLogFileName = (viewedLog.result as { fileName?: string }).fileName;
        assert.ok(viewedLogFileName && path.isAbsolute(viewedLogFileName));

        const copiedLogPath = await executeE2eControlCommand({ name: 'copyLogFilePath', appHostPath });
        assert.ok(path.isAbsolute(String(copiedLogPath.result)));

        await setTerminalCommandExecutionSuppressedForE2E(true);
        before = getCommandInvocationCount('aspire-vscode.viewResourceLogs');
        await executeE2eControlCommand({ name: 'viewResourceLogs', appHostPath, resourceName: 'e2e-worker' });
        await waitForCommandOutcome('aspire-vscode.viewResourceLogs', 'success', 60000, before);
        await setTerminalCommandExecutionSuppressedForE2E(false);

        await waitForResource('e2e-worker');
        await waitForResourceState('e2e-worker', ['Running'], 90000);

        before = getCommandInvocationCount('aspire-vscode.stopResource');
        let terminalBefore = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.stopResource', 'success', 60000, before);
        await waitForResourceTerminalCommand(workerResourceName, 'stop', terminalBefore);
        await waitForResourceState(workerResourceName, ['Stopped', 'Finished', 'Exited'], 90000);

        before = getCommandInvocationCount('aspire-vscode.startResource');
        terminalBefore = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'startResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.startResource', 'success', 60000, before);
        await waitForResourceTerminalCommand(workerResourceName, 'start', terminalBefore);

        before = getCommandInvocationCount('aspire-vscode.restartResource');
        terminalBefore = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'restartResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.restartResource', 'success', 60000, before);
        await waitForResourceTerminalCommand(workerResourceName, 'restart', terminalBefore);

        await setTerminalCommandExecutionSuppressedForE2E(true);
        before = getCommandInvocationCount('aspire-vscode.executeResourceCommandItem');
        terminalBefore = getTerminalCommandCount();
        assert.ok(enabledCommandItem, 'Expected enabled command tree item.');
        await executeE2eControlCommand({ name: 'executeResourceCommandItem', appHostPath, resourceName: workerResourceName, commandName: 'echo-arguments' }, { waitFor: 'started' });
        await chooseActiveQuickPick('Continue');
        await answerActiveInput('hello from command item', 'Message');
        await chooseActiveQuickPick('Alpha');
        await chooseActiveQuickPick('No');
        await answerActiveInput('10', 'Threshold');
        await answerActiveInput('secret-from-command-item', 'Token');
        await waitForCommandOutcome('aspire-vscode.executeResourceCommandItem', 'success', 60000, before);

        const commandItemTerminalCommand = await waitForTerminalCommand(
            event => event.subcommand.includes(`resource ${quoteExpectedShellArg(workerResourceName)}`) && event.subcommand.includes(quoteExpectedShellArg('echo-arguments')) && event.executionSuppressed,
            'resource command item with prompted arguments',
            60000,
            terminalBefore);
        assert.ok(commandItemTerminalCommand.containsRedactedArgs);
        assert.ok(!commandItemTerminalCommand.commandLine.includes('secret-from-command-item'));

        before = getCommandInvocationCount('aspire-vscode.executeResourceCommand');
        terminalBefore = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'executeResourceCommand', appHostPath, resourceName: workerResourceName }, { waitFor: 'started' });
        const quickPickLabels = await getActiveQuickPickLabels();
        assert.ok(quickPickLabels.includes('echo-arguments'));
        assert.ok(!quickPickLabels.includes('disabled-e2e-command'));
        assert.ok(!quickPickLabels.includes('hidden-e2e-command'));
        assert.ok(!quickPickLabels.includes('api-only-e2e-command'));
        assert.ok(!quickPickLabels.includes('unknown-state-e2e-command'));
        await chooseActiveQuickPick('echo-arguments');
        await chooseActiveQuickPick('Continue');
        await answerActiveInput('hello from e2e', 'Message');
        await chooseActiveQuickPick('Beta');
        await chooseActiveQuickPick('Yes');
        await answerActiveInput('42.5', 'Threshold');
        await answerActiveInput('secret-from-e2e', 'Token');
        await waitForCommandOutcome('aspire-vscode.executeResourceCommand', 'success', 60000, before);

        const resourceCommand = await waitForTerminalCommand(
            event => event.subcommand.includes('resource ') && event.subcommand.includes(quoteExpectedShellArg('echo-arguments')) && event.executionSuppressed,
            'resource command with prompted arguments',
            60000,
            terminalBefore);
        assert.ok(resourceCommand.containsRedactedArgs);
        assert.strictEqual(resourceCommand.additionalArgs, undefined);
        assert.ok(resourceCommand.commandLine.includes('[redacted command arguments]'));
        assert.ok(!resourceCommand.commandLine.includes('secret-from-e2e'));
    });
});

async function waitForResourceTerminalCommand(resourceName: string, command: string, afterCommandSequence: number): Promise<void> {
    await waitForTerminalCommand(
        event => event.subcommand.includes(`resource ${quoteExpectedShellArg(resourceName)}`) && event.subcommand.includes(` ${quoteExpectedShellArg(command)}`),
        `${command} terminal command for ${resourceName}`,
        60000,
        afterCommandSequence);
}

function quoteExpectedShellArg(arg: string): string {
    if (process.platform === 'win32') {
        return `"${arg.replace(/`/g, '``').replace(/"/g, '`"').replace(/\$/g, '`$')}"`;
    }

    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
}
