import * as assert from 'assert';
import * as path from 'path';
import { findResource, getCommandInvocationCount, getDebugLaunchCount, getTerminalCommandCount, getTreeAppHostLabel, isSamePath, waitForAppHostLaunching, waitForCommandOutcome, waitForDashboardUrl, waitForDebugConsoleOutput, waitForDebugLaunch, waitForExtensionState, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForResourceState, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost, waitForWorkspaceAppHostCandidate } from './helpers/assertions';
import { assertClipboardMatchesLastExpectationForE2E, clearWorkspaceFolderCliPathsForE2E, createAdditionalAppHostCandidate, executeE2eControlCommand, removeAdditionalAppHostCandidate, restoreClipboardSnapshotForE2E, restoreE2eCliPathForE2E, restoreWorkspaceCliPath, restoreWorkspaceFoldersForE2E, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, setE2eCliPathForE2E, setTerminalCommandExecutionSuppressedForE2E, setWorkspaceFolderCliPathForE2E, setWorkspaceFoldersForE2E, snapshotClipboardForE2E, stopPrimaryAppHostIfRunning, writeBaselineActionCliWrapper, writeGatedDeployActionCliWrapper, writeLegacyPipelineActionCliWrapper } from './helpers/fixtures';
import { getCliPath, getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { readExtensionLogs } from './helpers/logs';
import { answerActiveInput, answerActiveInputByMessage, cancelActiveInput, chooseActiveQuickPick, getActiveQuickPickLabels, openAspireView, waitForChildTreeItem, waitForTreeItem, waitForTreeItemDescription, waitForWorkbenchText, waitForWorkbenchTextAfterIntegratedBrowserNavigation } from './helpers/vscode';

interface ActiveEditorInfo {
    uri?: string;
    fileName?: string;
    text?: string;
}

suite('Aspire tree action command E2E', function () {
    this.timeout(300000);

    teardown(async () => {
        await runE2eTeardown([
            () => restoreClipboardSnapshotForE2E(),
            () => setCliUnavailableForE2E(false),
            () => setDebugLaunchSuppressedForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => clearWorkspaceFolderCliPathsForE2E(),
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceFoldersForE2E(),
            () => restoreWorkspaceCliPath(),
            () => removeAdditionalAppHostCandidate('AspireE2E.SecondaryActions'),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoRunningAppHost(),
        ], 'Tree action E2E teardown failed.');
    });

    test('routes all AppHost actions to the exact secondary AppHost and owning CLI target', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const primaryAppHostPath = (await waitForWorkspaceAppHost()).state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const primaryFolderPath = path.dirname(primaryAppHostPath);
        const secondaryAppHostPath = createAdditionalAppHostCandidate('AspireE2E.SecondaryActions', 'single-file');
        const secondaryFolderPath = path.dirname(secondaryAppHostPath);
        const secondaryCliPath = writeBaselineActionCliWrapper('aspire-secondary-actions');

        await setE2eCliPathForE2E(undefined);
        const workspaceFolders = await setWorkspaceFoldersForE2E([
            { folderPath: primaryFolderPath, name: 'primary' },
            { folderPath: secondaryFolderPath, name: 'secondary' },
        ]);
        const secondaryFolder = workspaceFolders.find(folder => isSamePath(folder.fileName, secondaryFolderPath));
        assert.ok(secondaryFolder, `Expected secondary workspace folder in ${JSON.stringify(workspaceFolders)}.`);
        const expectedCliTargetKey = `workspaceFolder:${secondaryFolder.uri}`;
        const secondaryCliConfiguration = await setWorkspaceFolderCliPathForE2E(secondaryFolderPath, secondaryCliPath);
        assert.strictEqual(secondaryCliConfiguration.targetKey, expectedCliTargetKey);
        assert.ok(isSamePath(secondaryCliConfiguration.cliPath, secondaryCliPath));
        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
        await waitForWorkspaceAppHostCandidate(secondaryAppHostPath);
        assert.ok(!isSamePath(primaryAppHostPath, secondaryAppHostPath));

        await setDebugLaunchSuppressedForE2E(true);

        await assertAppHostActionLaunch(
            { name: 'deployAppHostAction', appHostPath: secondaryAppHostPath },
            'aspire-vscode.deployAppHost',
            secondaryAppHostPath,
            secondaryCliPath,
            expectedCliTargetKey,
            'deploy',
            false);
        await assertAppHostActionLaunch(
            { name: 'publishAppHostAction', appHostPath: secondaryAppHostPath },
            'aspire-vscode.publishAppHost',
            secondaryAppHostPath,
            secondaryCliPath,
            expectedCliTargetKey,
            'publish',
            false);
        await assertAppHostActionLaunch(
            { name: 'runPipelineStepAppHostAction', appHostPath: secondaryAppHostPath },
            'aspire-vscode.runPipelineStepAppHost',
            secondaryAppHostPath,
            secondaryCliPath,
            expectedCliTargetKey,
            'do',
            true,
            'secondary-run-step');
        await assertAppHostActionLaunch(
            { name: 'debugPipelineStepAppHostAction', appHostPath: secondaryAppHostPath },
            'aspire-vscode.debugPipelineStepAppHost',
            secondaryAppHostPath,
            secondaryCliPath,
            expectedCliTargetKey,
            'do',
            false,
            'secondary-debug-step');
    });

    test('uses the legacy pipeline input fallback for trimmed, invalid, and canceled input', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const appHostPath = (await waitForWorkspaceAppHost()).state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        await setE2eCliPathForE2E(writeLegacyPipelineActionCliWrapper('aspire-legacy-pipeline-actions'));
        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await setDebugLaunchSuppressedForE2E(true);

        const validLaunchBefore = getDebugLaunchCount();
        const validInvocationBefore = getCommandInvocationCount('aspire-vscode.runPipelineStepAppHost');
        await executeE2eControlCommand({ name: 'runPipelineStepAppHostAction', appHostPath }, { waitFor: 'started' });
        await answerActiveInput('  legacy-step  ', 'deploy');
        await waitForCommandOutcome('aspire-vscode.runPipelineStepAppHost', 'success', 60000, validInvocationBefore);
        const validLaunch = await waitForDebugLaunch(
            event => event.command === 'do' && event.doStep === 'legacy-step',
            'trimmed legacy pipeline launch',
            60000,
            validLaunchBefore);
        assert.strictEqual(validLaunch.noDebug, true);

        const whitespaceLaunchBefore = getDebugLaunchCount();
        const whitespaceInvocationBefore = getCommandInvocationCount('aspire-vscode.runPipelineStepAppHost');
        await executeE2eControlCommand({ name: 'runPipelineStepAppHostAction', appHostPath }, { waitFor: 'started' });
        await answerActiveInput('   ', 'deploy');
        await waitForWorkbenchText('Enter a pipeline step name.');
        assert.strictEqual(getDebugLaunchCount(), whitespaceLaunchBefore);
        await cancelActiveInput();
        await waitForCommandOutcome('aspire-vscode.runPipelineStepAppHost', 'canceled', 60000, whitespaceInvocationBefore);
        assert.strictEqual(getDebugLaunchCount(), whitespaceLaunchBefore);

        const canceledLaunchBefore = getDebugLaunchCount();
        const canceledInvocationBefore = getCommandInvocationCount('aspire-vscode.debugPipelineStepAppHost');
        await executeE2eControlCommand({ name: 'debugPipelineStepAppHostAction', appHostPath }, { waitFor: 'started' });
        await cancelActiveInput();
        await waitForCommandOutcome('aspire-vscode.debugPipelineStepAppHost', 'canceled', 60000, canceledInvocationBefore);
        assert.strictEqual(getDebugLaunchCount(), canceledLaunchBefore);
    });

    test('lets the current CLI select and execute run and debug pipeline steps with exact final args', async function () {
        if (process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true') {
            return;
        }

        this.timeout(600000);
        await openAspireView();
        await waitForRepositoryIdle();
        const appHostPath = (await waitForWorkspaceAppHost()).state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        await restoreE2eCliPathForE2E();
        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const runLaunchBefore = getDebugLaunchCount();
        const runInvocationBefore = getCommandInvocationCount('aspire-vscode.runPipelineStepAppHost');
        await executeE2eControlCommand({ name: 'runPipelineStepAppHostAction', appHostPath }, { waitFor: 'started' });
        await answerActiveInput('e2e-run-action-step', 'Select a pipeline step to execute', 120000);
        await waitForCommandOutcome('aspire-vscode.runPipelineStepAppHost', 'success', 60000, runInvocationBefore);
        const runLaunch = await waitForDebugLaunch(
            event => event.command === 'do' && event.noDebug && event.doStep === 'e2e-run-action-step',
            'current CLI run pipeline launch',
            60000,
            runLaunchBefore);
        assert.strictEqual(runLaunch.cliPath, getCliPath());
        await waitForDebugConsoleOutput('E2E run action pipeline step completed', appHostPath, 180000);
        await waitForNoDebugSessions(180000);

        const debugLaunchBefore = getDebugLaunchCount();
        const debugInvocationBefore = getCommandInvocationCount('aspire-vscode.debugPipelineStepAppHost');
        await executeE2eControlCommand({ name: 'debugPipelineStepAppHostAction', appHostPath }, { waitFor: 'started' });
        await answerActiveInput('e2e-debug-action-step', 'Select a pipeline step to execute', 120000);
        await waitForCommandOutcome('aspire-vscode.debugPipelineStepAppHost', 'success', 60000, debugInvocationBefore);
        const debugLaunch = await waitForDebugLaunch(
            event => event.command === 'do' && !event.noDebug && event.doStep === 'e2e-debug-action-step',
            'current CLI debug pipeline launch',
            60000,
            debugLaunchBefore);
        assert.strictEqual(debugLaunch.cliPath, getCliPath());
        await waitForDebugConsoleOutput('E2E debug action pipeline step completed', appHostPath, 180000);
        await waitForNoDebugSessions(180000);

        const spawnLines = readExtensionLogs()
            .split(/\r?\n/)
            .filter(line => line.includes('Spawning Aspire CLI process:') && line.includes(` do `) && line.includes(`--apphost ${appHostPath}`));
        assert.ok(
            spawnLines.some(line => line.includes(' do e2e-run-action-step --nologo ') && !line.includes('--start-debug-session')),
            `Expected run pipeline args without --start-debug-session. Spawn lines: ${JSON.stringify(spawnLines)}`);
        assert.ok(
            spawnLines.some(line => line.includes(' do e2e-debug-action-step --start-debug-session --nologo ')),
            `Expected debug pipeline args with --start-debug-session. Spawn lines: ${JSON.stringify(spawnLines)}`);
    });

    test('does not duplicate an AppHost launch while the same AppHost is reserved', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const appHostPath = (await waitForWorkspaceAppHost()).state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        await restoreE2eCliPathForE2E();

        const runInvocationBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForAppHostLaunching(appHostPath);
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, runInvocationBefore);

        const launchesBeforeDuplicate = getDebugLaunchCount();
        const deployInvocationBefore = getCommandInvocationCount('aspire-vscode.deployAppHost');
        await executeE2eControlCommand({ name: 'deployAppHostAction', appHostPath });
        await waitForCommandOutcome('aspire-vscode.deployAppHost', 'canceled', 60000, deployInvocationBefore);
        assert.strictEqual(getDebugLaunchCount(), launchesBeforeDuplicate);
    });

    test('keeps one durable operation per AppHost while a deploy session is in flight', async () => {
        const section = await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const appHostLabel = getTreeAppHostLabel(discovered.state);
        const gatedCli = writeGatedDeployActionCliWrapper('aspire-gated-deploy-actions');
        try {
            await setE2eCliPathForE2E(gatedCli.cliPath);
            const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
            await executeE2eControlCommand({ name: 'refreshAppHosts' });
            await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
            await waitForRepositoryIdle();
            await waitForWorkspaceAppHost();

            // A durable operation only exists while a real launch owns the AppHost, so this test
            // deliberately does not suppress the debug launch. The gated CLI blocks inside `deploy`
            // until it is released, which holds the operation open without any timing assumption.
            const launchBefore = getDebugLaunchCount();
            const deployBefore = getCommandInvocationCount('aspire-vscode.deployAppHost');
            await executeE2eControlCommand({ name: 'deployAppHostAction', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.deployAppHost', 'success', 120000, deployBefore);
            await gatedCli.waitForDeployRequest();
            await waitForTreeItemDescription(section, appHostLabel, 'Deploying...', 60000);

            const duplicateDeployBefore = getCommandInvocationCount('aspire-vscode.deployAppHost');
            await executeE2eControlCommand({ name: 'deployAppHostAction', appHostPath });
            await waitForCommandOutcome('aspire-vscode.deployAppHost', 'canceled', 60000, duplicateDeployBefore);

            const duplicatePublishBefore = getCommandInvocationCount('aspire-vscode.publishAppHost');
            await executeE2eControlCommand({ name: 'publishAppHostAction', appHostPath });
            await waitForCommandOutcome('aspire-vscode.publishAppHost', 'canceled', 60000, duplicatePublishBefore);
            assert.strictEqual(getDebugLaunchCount(), launchBefore + 1);

            gatedCli.releaseDeploy();
            await waitForNoDebugSessions(120000);

            // The operation is owned by its session, so a deploy accepted after that session ends
            // proves the durable state was released rather than leaked onto the AppHost.
            const releasedDeployBefore = getCommandInvocationCount('aspire-vscode.deployAppHost');
            const releasedLaunchBefore = getDebugLaunchCount();
            await executeE2eControlCommand({ name: 'deployAppHostAction', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.deployAppHost', 'success', 120000, releasedDeployBefore);
            await waitForDebugLaunch(
                event => event.command === 'deploy',
                'deploy launch after the durable operation ended',
                60000,
                releasedLaunchBefore);
            await waitForNoDebugSessions(120000);
        }
        finally {
            try {
                gatedCli.releaseDeploy();
            }
            finally {
                gatedCli.cleanup();
            }
            await waitForNoDebugSessions(120000);
        }
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

        await snapshotClipboardForE2E();
        await executeE2eControlCommand({ name: 'copyAppHostPath', appHostPath });
        await assertClipboardMatchesLastExpectationForE2E();

        const openedSource = await executeE2eControlCommand({ name: 'openAppHostSource', appHostPath });
        assert.ok(String((openedSource.result as { fileName?: string }).fileName).endsWith(path.join('AspireE2E.AppHost', 'AppHost.cs')));

        const viewedSource = await executeE2eControlCommand({ name: 'viewAppHostSource', appHostPath });
        assert.ok(String((viewedSource.result as { uri?: string }).uri).startsWith('aspire-source:'));

        await executeE2eControlCommand({ name: 'copyResourceName', appHostPath, resourceName: 'e2e-worker' });
        await assertClipboardMatchesLastExpectationForE2E();

        const endpointUrl = workerResource.urls?.find(url => !url.isInternal)?.url ?? workerResource.urls?.[0]?.url;
        assert.ok(endpointUrl, 'Expected e2e-worker to expose an endpoint URL.');
        assert.ok(endpointUrl.startsWith('http'));
        await executeE2eControlCommand({ name: 'copyEndpointUrl', appHostPath, resourceName: 'e2e-worker' });
        await assertClipboardMatchesLastExpectationForE2E();

        before = getCommandInvocationCount('aspire-vscode.openInIntegratedBrowser');
        const openedEndpoint = await executeE2eControlCommand({ name: 'openInIntegratedBrowser', appHostPath, resourceName: 'e2e-worker' });
        await waitForCommandOutcome('aspire-vscode.openInIntegratedBrowser', 'success', 60000, before);
        assert.strictEqual((openedEndpoint.result as { url?: string }).url, endpointUrl);
        await waitForWorkbenchTextAfterIntegratedBrowserNavigation(new URL(endpointUrl).host);
        assert.strictEqual(await waitForHttpText(endpointUrl, 'ok'), 'ok');

        const viewedLog = await executeE2eControlCommand({ name: 'viewAppHostLogFile', appHostPath });
        const viewedLogFileName = (viewedLog.result as { fileName?: string }).fileName;
        assert.ok(viewedLogFileName && path.isAbsolute(viewedLogFileName));

        await executeE2eControlCommand({ name: 'copyLogFilePath', appHostPath });
        await assertClipboardMatchesLastExpectationForE2E();

        let terminalBefore: number;

        await setTerminalCommandExecutionSuppressedForE2E(true);
        before = getCommandInvocationCount('aspire-vscode.viewResourceLogs');
        await executeE2eControlCommand({ name: 'viewResourceLogs', appHostPath, resourceName: 'e2e-worker' });
        await waitForCommandOutcome('aspire-vscode.viewResourceLogs', 'success', 60000, before);
        await setTerminalCommandExecutionSuppressedForE2E(false);

        await waitForResource('e2e-worker');
        await waitForResourceState('e2e-worker', ['Running'], 90000);

        await setTerminalCommandExecutionSuppressedForE2E(true);
        try {
            before = getCommandInvocationCount('aspire-vscode.openResourceTerminal');
            terminalBefore = getTerminalCommandCount();
            await executeE2eControlCommand({ name: 'openResourceTerminal', appHostPath, resourceName: workerResourceName });
            await waitForCommandOutcome('aspire-vscode.openResourceTerminal', 'success', 60000, before);
            await waitForTerminalCommand(
                event => event.subcommand.includes(`terminal attach ${quoteExpectedShellArg(workerResourceName)}`) && event.executionSuppressed,
                'open resource terminal command',
                60000,
                terminalBefore);

            // e2e-terminal is registered with .WithTerminal(), so the real Aspire CLI surfaces
            // terminal.enabled and terminal.replicaIndex over the backchannel. Opening its terminal
            // must therefore append --replica derived from that metadata, unlike e2e-worker above
            // which has no terminal annotation and emits no --replica. This proves the terminal
            // properties flow end-to-end through a real CLI process and drive the Open terminal action.
            const terminalResourceState = await waitForResourceState('e2e-terminal', ['Running'], 180000);
            const terminalResource = findResource(terminalResourceState.state, 'e2e-terminal');
            assert.ok(terminalResource, 'Expected e2e-terminal to be present after AppHost startup.');
            const terminalResourceName = terminalResource.name;
            terminalBefore = getTerminalCommandCount();
            before = getCommandInvocationCount('aspire-vscode.openResourceTerminal');
            await executeE2eControlCommand({ name: 'openResourceTerminal', appHostPath, resourceName: terminalResourceName });
            await waitForCommandOutcome('aspire-vscode.openResourceTerminal', 'success', 60000, before);
            await waitForTerminalCommand(
                event => event.subcommand.includes(`terminal attach ${quoteExpectedShellArg(terminalResourceName)}`)
                    && event.subcommand.includes('--replica')
                    && event.executionSuppressed,
                'open terminal-enabled resource terminal command',
                60000,
                terminalBefore);
        } finally {
            await setTerminalCommandExecutionSuppressedForE2E(false);
        }

        await waitForResource('e2e-worker');
        await waitForResourceState('e2e-worker', ['Running'], 90000);

        // Resource lifecycle commands now execute over the hidden CLI backchannel instead of being
        // typed into the visible Aspire terminal, so there is no terminal command to observe or to
        // suppress. Drive the real command and assert on the instrumented command outcome, the lack of
        // a visible terminal command, and the resulting resource state transitions.
        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.stopResource');
        await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.stopResource', 'success', 60000, before);
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        await waitForResourceState(workerResourceName, ['Exited', 'Finished', 'Stopped'], 90000);

        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.startResource');
        await executeE2eControlCommand({ name: 'startResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.startResource', 'success', 60000, before);
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        await waitForResourceState(workerResourceName, ['Running'], 90000);

        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.restartResource');
        await executeE2eControlCommand({ name: 'restartResource', appHostPath, resourceName: workerResourceName });
        await waitForCommandOutcome('aspire-vscode.restartResource', 'success', 60000, before);
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        await waitForResourceState(workerResourceName, ['Running'], 90000);

        // The echo-arguments command returns a value, which the extension now renders in a read-only
        // editor rather than streaming raw stdout to a shared terminal. Prompted secret arguments are
        // passed to the hidden CLI process (not echoed to a terminal others can scroll); redaction of
        // those arguments in spawn diagnostics is covered by the cliSpawn unit tests. Here we drive
        // the interactive argument prompts and assert the real Extension Host opens the aspire-source
        // output editor without creating any new terminal command events.
        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.executeResourceCommandItem');
        assert.ok(enabledCommandItem, 'Expected enabled command tree item.');
        await executeE2eControlCommand({ name: 'executeResourceCommandItem', appHostPath, resourceName: workerResourceName, commandName: 'echo-arguments' }, { waitFor: 'started' });
        await chooseActiveQuickPick('Continue');
        await answerActiveInput('hello from command item', 'Message');
        await chooseActiveQuickPick('Alpha');
        await chooseActiveQuickPick('No');
        await answerActiveInput('10', 'Threshold');
        await answerActiveInput('secret-from-command-item', 'Token');
        await waitForCommandOutcome('aspire-vscode.executeResourceCommandItem', 'success', 60000, before);
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        const commandItemEditor = await waitForResourceCommandOutputEditor(workerResourceName, 'echo-arguments', 'hello from command item');

        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.executeResourceCommand');
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
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        const commandPaletteEditor = await waitForResourceCommandOutputEditor(workerResourceName, 'echo-arguments', 'hello from e2e');
        assert.notStrictEqual(commandPaletteEditor.text, commandItemEditor.text);

        terminalBefore = getTerminalCommandCount();
        before = getCommandInvocationCount('aspire-vscode.codeLensResourceAction');
        await executeE2eControlCommand({
            name: 'executeCodeLensResourceAction',
            resourceName: workerResourceName,
            commandName: 'echo-arguments',
            appHostPath,
        }, { waitFor: 'started' });
        await chooseActiveQuickPick('Continue');
        await answerActiveInput('hello from codelens', 'Message');
        await chooseActiveQuickPick('Alpha');
        await chooseActiveQuickPick('No');
        await answerActiveInput('7', 'Threshold');
        await answerActiveInput('secret-from-codelens', 'Token');
        await waitForCommandOutcome('aspire-vscode.codeLensResourceAction', 'success', 60000, before);
        assert.strictEqual(getTerminalCommandCount(), terminalBefore);
        const codeLensEditor = await waitForResourceCommandOutputEditor(workerResourceName, 'echo-arguments', 'hello from codelens');
        assert.notStrictEqual(codeLensEditor.text, commandPaletteEditor.text);
    });
});

type AppHostActionControlCommand =
    | { name: 'deployAppHostAction'; appHostPath: string }
    | { name: 'publishAppHostAction'; appHostPath: string }
    | { name: 'runPipelineStepAppHostAction'; appHostPath: string }
    | { name: 'debugPipelineStepAppHostAction'; appHostPath: string };

async function assertAppHostActionLaunch(
    controlCommand: AppHostActionControlCommand,
    commandId: string,
    appHostPath: string,
    cliPath: string,
    cliTargetKey: string,
    expectedCommand: 'deploy' | 'publish' | 'do',
    expectedNoDebug: boolean,
    pipelineStep?: string,
): Promise<void> {
    const launchBefore = getDebugLaunchCount();
    const invocationBefore = getCommandInvocationCount(commandId);
    await executeE2eControlCommand(controlCommand, { waitFor: 'started' });
    if (pipelineStep) {
        await answerActiveInput(`  ${pipelineStep}  `, 'deploy');
    }
    await waitForCommandOutcome(commandId, 'success', 60000, invocationBefore);
    const launch = await waitForDebugLaunch(
        event => event.command === expectedCommand
            && event.noDebug === expectedNoDebug
            && event.doStep === pipelineStep,
        `${commandId} launch for secondary AppHost`,
        60000,
        launchBefore);

    assert.ok(isSamePath(launch.appHostPath, appHostPath), `Expected ${commandId} to target '${appHostPath}', got '${launch.appHostPath}'.`);
    assert.ok(isSamePath(launch.cliPath ?? '', cliPath), `Expected ${commandId} to use CLI '${cliPath}', got '${launch.cliPath}'.`);
    assert.strictEqual(launch.cliTargetKey, cliTargetKey);
}

async function waitForResourceCommandOutputEditor(resourceName: string, commandName: string, expectedText: string, timeoutMs = 60000): Promise<ActiveEditorInfo> {
    const started = Date.now();
    let lastEditor: ActiveEditorInfo | undefined;
    const resourceCommandOutputName = `${resourceName}-${commandName}`.replace(/[^A-Za-z0-9._-]+/g, '_');

    while (Date.now() - started < timeoutMs) {
        const result = (await executeE2eControlCommand({ name: 'getActiveEditor' })).result as ActiveEditorInfo;
        lastEditor = result;
        const uri = result.uri ?? '';
        if (uri.startsWith('aspire-source:') && uri.includes(`${resourceCommandOutputName}-output.txt`) && result.text?.includes(expectedText)) {
            return result;
        }

        await delay(200);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for resource command output editor '${resourceName}/${commandName}' containing '${expectedText}'. Last active editor: ${JSON.stringify(lastEditor)}`);
}

function quoteExpectedShellArg(arg: string): string {
    if (process.platform === 'win32') {
        return `"${arg.replace(/`/g, '``').replace(/"/g, '`"').replace(/\$/g, '`$')}"`;
    }

    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}
