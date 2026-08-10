import * as assert from 'assert';
import { findRunningAppHost, getCommandInvocationCount, getResources, getTerminalCommandCount, getTreeAppHostLabel, isSamePath, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { assertClipboardMatchesLastExpectationForE2E, captureWorkspaceAppHostPathClipboardExpectationForE2E, executeE2eControlCommand, getCliWrapperInvocationCount, getCliWrapperInvocations, restoreClipboardSnapshotForE2E, restoreE2eCliPathForE2E, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setE2eCliPathForE2E, setTerminalCommandExecutionSuppressedForE2E, snapshotClipboardForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, touchPrimaryAppHostProject, writeDelayedPsCliWrapper, writeStreamingDiscoveryCliWrapper, writeTrackedDelayedPsCliWrapper, writeTrackedStreamingDiscoveryCliWrapper } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { cancelActiveInput, clickTreeItem, executeCommandFromPalette, openAspireView, waitForChildTreeItem, waitForNotificationMessage, waitForTreeItem, waitForWorkbenchText } from './helpers/vscode';

suite('Aspire AppHost tree E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => restoreClipboardSnapshotForE2E(),
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'switchToWorkspaceView' }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'AppHost tree E2E teardown failed.');
    });

    test('discovers the workspace AppHost and renders it in the Aspire view', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const stateFile = await waitForWorkspaceAppHost();
        const label = getTreeAppHostLabel(stateFile.state);
        const section = await openAspireView();

        const item = await waitForTreeItem(section, label);
        assert.strictEqual(await item.getLabel(), label);
        assert.ok(stateFile.state.workspaceAppHostCandidatePaths.length >= 1);
    });

    test('shows streamed candidates while AppHost discovery is still running', async () => {
        await openAspireView();
        await waitForWorkspaceAppHost();

        await setE2eCliPathForE2E(writeStreamingDiscoveryCliWrapper(15_000));
        const invocationCountBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });

        const partialState = await waitForExtensionState(
            file => file.state.isWorkspaceAppHostDiscoveryComplete === false &&
                file.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath())),
            'streamed AppHost candidate before discovery completes',
            30000);
        assert.strictEqual(partialState.state.isWorkspaceAppHostDiscoveryComplete, false);
        const partialSection = await openAspireView();
        const partialItem = await waitForTreeItem(partialSection, getTreeAppHostLabel(partialState.state));
        assert.strictEqual(await partialItem.getLabel(), getTreeAppHostLabel(partialState.state));
        await waitForWorkbenchText('Discovering AppHosts...');

        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 30000, invocationCountBefore);
        const finalState = await waitForRepositoryIdle();
        assert.strictEqual(finalState.state.isWorkspaceAppHostDiscoveryComplete, true);
        assert.ok(finalState.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath())));
    });

    test('file changes queue one rediscovery', async () => {
        await openAspireView();
        await waitForWorkspaceAppHost();

        const wrapper = writeTrackedStreamingDiscoveryCliWrapper();
        await setE2eCliPathForE2E(wrapper.cliPath);
        const invocationCountBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });
        await waitForExtensionState(
            file => file.state.isWorkspaceAppHostDiscoveryComplete === false,
            'streaming AppHost discovery to start');

        for (let i = 0; i < 3; i++) {
            touchPrimaryAppHostProject();
            await new Promise(resolve => setTimeout(resolve, 400));
        }

        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 30000, invocationCountBefore);
        await waitForRepositoryIdle();
        assert.strictEqual(getCliWrapperInvocationCount(wrapper.invocationLogPath), 2);
    });

    test('refresh shows loading until an AppHost appears', async () => {
        await openAspireView();
        await waitForWorkspaceAppHost();

        await setE2eCliPathForE2E(writeStreamingDiscoveryCliWrapper());
        const invocationCountBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });

        await waitForWorkspaceRediscoveryLoading('workspace AppHost refresh loading state');

        const candidateState = await waitForExtensionState(
            file => !file.state.isRepositoryLoading
                && file.state.isWorkspaceAppHostDiscoveryComplete === false
                && file.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath())),
            'first streamed AppHost candidate to clear refresh loading',
            30000);
        assert.strictEqual(candidateState.state.isRepositoryLoading, false);

        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 30000, invocationCountBefore);
        await waitForRepositoryIdle();
    });

    test('global refresh pulls authoritative AppHost state without workspace discovery', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        await waitForExtensionState(
            file => file.state.viewMode === 'global' && !file.state.isRepositoryLoading,
            'global AppHost view to become idle');

        const wrapper = writeTrackedDelayedPsCliWrapper();
        await setE2eCliPathForE2E(wrapper.cliPath);
        await executeE2eControlCommand({ name: 'globalRefreshAppHosts' }, { waitFor: 'started' });

        const loadingState = await waitForExtensionState(
            file => file.state.viewMode === 'global' && file.state.isRepositoryLoading,
            'global AppHost refresh loading state',
            30000);
        assert.strictEqual(loadingState.state.isRepositoryLoading, true);
        await waitForWorkbenchText('Searching for AppHosts...', 30000);

        const freshState = await waitForExtensionState(
            file => file.state.viewMode === 'global' && !file.state.isRepositoryLoading,
            'fresh global AppHost state',
            30000);
        assert.strictEqual(freshState.state.isRepositoryLoading, false);

        const invocations = getCliWrapperInvocations(wrapper.invocationLogPath);
        assert.ok(
            invocations.some(args => args[0] === 'ps' && !args.includes('--follow')),
            'global refresh should pull an authoritative ps snapshot');
        assert.ok(
            invocations.every(args => args[0] !== 'ls'),
            'global refresh should not start workspace discovery');
    });

    test('global refresh clears loading after switching to the workspace view', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        await waitForExtensionState(
            file => file.state.viewMode === 'global' && !file.state.isRepositoryLoading,
            'global AppHost view to become idle');

        await setE2eCliPathForE2E(writeDelayedPsCliWrapper(3_000));
        await executeE2eControlCommand({ name: 'globalRefreshAppHosts' }, { waitFor: 'started' });
        await waitForExtensionState(
            file => file.state.viewMode === 'global' && file.state.isRepositoryLoading,
            'global AppHost refresh loading state',
            30000);

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });
        const workspaceLoadingState = await waitForExtensionState(
            file => file.state.viewMode === 'workspace' && file.state.isRepositoryLoading,
            'workspace view to retain the shared ps refresh loading state',
            30000);
        assert.strictEqual(workspaceLoadingState.state.isRepositoryLoading, true);

        const freshWorkspaceState = await waitForExtensionState(
            file => file.state.viewMode === 'workspace' && !file.state.isRepositoryLoading,
            'fresh ps snapshot to clear workspace loading',
            30000);
        assert.strictEqual(freshWorkspaceState.state.isRepositoryLoading, false);

        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        const freshGlobalState = await waitForExtensionState(
            file => file.state.viewMode === 'global' && !file.state.isRepositoryLoading,
            'completed ps snapshot to clear hidden global loading',
            30000);
        assert.strictEqual(freshGlobalState.state.isRepositoryLoading, false);
    });

    test('running AppHosts appear before slow discovery results', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostLabel = getTreeAppHostLabel(discovered.state);
        let section = await openAspireView();

        const idleItem = await waitForTreeItem(section, appHostLabel);
        await idleItem.expand();
        await clickTreeItem(section, 'Run AppHost');
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success');
        await waitForRunningAppHost();

        // Keep workspace discovery pending long enough for the fresh aspire ps snapshot to
        // independently restore the running AppHost to the tree.
        await setE2eCliPathForE2E(writeStreamingDiscoveryCliWrapper(5_000, 5_000));
        const invocationCountBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });

        await waitForWorkspaceRediscoveryLoading('workspace AppHost refresh loading state before running AppHost refresh');

        const runningBeforeDiscovery = await waitForExtensionState(
            file => !file.state.isRepositoryLoading
                && file.state.isWorkspaceAppHostDiscoveryComplete === false
                && file.state.workspaceAppHostCandidatePaths.length === 0
                && findRunningAppHost(file.state) !== undefined,
            'running AppHost to clear loading before workspace discovery produces a candidate',
            30000);
        assert.ok(findRunningAppHost(runningBeforeDiscovery.state));

        section = await openAspireView();
        const runningItem = await waitForTreeItem(section, appHostLabel);
        assert.strictEqual(await runningItem.getLabel(), appHostLabel);

        const candidateAfterRunning = await waitForExtensionState(
            file => file.state.isWorkspaceAppHostDiscoveryComplete === false
                && file.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath()))
                && findRunningAppHost(file.state) !== undefined,
            'streamed workspace AppHost candidate after the running AppHost is restored',
            30000);
        assert.strictEqual(candidateAfterRunning.state.isRepositoryLoading, false);

        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 30000, invocationCountBefore);
        await waitForRepositoryIdle();
    });

    test('clicking the Path tree item copies the AppHost path and shows a confirmation notification', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await snapshotClipboardForE2E();
        const discovered = await waitForWorkspaceAppHost();
        const appHostLabel = getTreeAppHostLabel(discovered.state);
        const section = await openAspireView();

        // The Path row only appears under an idle (non-running) workspace AppHost item, so exercise
        // it before starting the AppHost. See https://github.com/microsoft/aspire/issues/18578.
        const idleItem = await waitForTreeItem(section, appHostLabel);
        await idleItem.expand();

        // Labels below match loc/strings.ts (appHostPathLabel / appHostPathCopiedToClipboard); the
        // E2E host runs in English so the literals are stable, mirroring other tree-item labels
        // asserted in this suite (e.g. 'Run AppHost').
        const pathItem = await waitForChildTreeItem(idleItem, 'Path');
        await captureWorkspaceAppHostPathClipboardExpectationForE2E();
        await pathItem.click();

        // The notification only fires after a successful copy, so its appearance proves the click
        // routed through aspire-vscode.copyAppHostPath rather than reading a stale clipboard value.
        await waitForNotificationMessage('AppHost path copied to clipboard.');
        await assertClipboardMatchesLastExpectationForE2E();
    });

    test('runs, shows resources and dashboard state, routes resource commands, and stops from the tree', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostLabel = getTreeAppHostLabel(discovered.state);
        let section = await openAspireView();

        const idleItem = await waitForTreeItem(section, appHostLabel);
        await idleItem.expand();
        await clickTreeItem(section, 'Run AppHost');
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success');

        const running = await waitForRunningAppHost();
        assert.ok(running.state.appHosts.length >= 1 || running.state.workspaceAppHost);

        const workerState = await waitForResource('e2e-worker');
        const dashboard = await waitForDashboardUrl();
        assert.ok(dashboard.dashboardUrl?.startsWith('http'));

        section = await openAspireView();
        const runningItem = await waitForTreeItem(section, appHostLabel);
        await runningItem.expand();
        const workerItem = await waitForTreeItem(section, 'e2e-worker');
        assert.ok(workerItem);
        assert.ok(getResources(workerState.state).some(resource => (resource.displayName ?? resource.name) === 'e2e-worker'));

        await executeE2eControlCommand({ name: 'executeResourceCommand', resourceName: 'e2e-worker' }, { waitFor: 'started' });
        await cancelActiveInput();
        await waitForCommandOutcome('aspire-vscode.executeResourceCommand', 'canceled');

        await setTerminalCommandExecutionSuppressedForE2E(true);
        try {
            const beforeTerminalCommand = getTerminalCommandCount();
            await executeE2eControlCommand(
                { name: 'stopAppHost', appHostPath: discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath() },
                { waitFor: 'started' });

            await waitForTerminalCommand(
                event => event.executionSuppressed && event.subcommand.startsWith('stop '),
                'suppressed AppHost stop terminal routing',
                60000,
                beforeTerminalCommand);
            await waitForCommandOutcome('aspire-vscode.stopAppHost', 'success');
        } finally {
            await setTerminalCommandExecutionSuppressedForE2E(false);
        }

        await stopPrimaryAppHostIfRunning();
        await waitForNoRunningAppHost();
    });

    async function waitForWorkspaceRediscoveryLoading(description: string): Promise<void> {
        const loadingState = await waitForExtensionState(
            file => file.state.isRepositoryLoading
                && file.state.isWorkspaceAppHostDiscoveryComplete === false
                && file.state.workspaceAppHostPath === undefined
                && file.state.workspaceAppHostCandidatePaths.length === 0,
            description,
            30000);
        assert.strictEqual(loadingState.state.isRepositoryLoading, true);

        const loadingText = await waitForWorkbenchText('Searching for AppHosts...', 30000);
        assert.ok(!loadingText.includes('No Aspire AppHosts detected in this workspace.'));
    }

    test('workspace view return clears stale stopped AppHost after returning to Aspire view', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });

        // Prior tests can leave a debug session attached to the same AppHost path.
        // Normalize to a no-debug/no-running baseline before validating stale-state clearing.
        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions(120000);
        await stopAppHostIfRunning(appHostPath);
        await waitForNoRunningAppHost(120000, appHostPath);

        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success');
        await waitForRunningAppHost();

        await executeCommandFromPalette('workbench.view.explorer');
        await stopAppHostIfRunning(appHostPath);

        await openAspireView();
        await waitForNoRunningAppHost(120000, appHostPath);
    });

    test('global view return clears stale stopped AppHost after returning to Aspire view', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await executeE2eControlCommand({ name: 'switchToGlobalView' });

        // Prior tests can leave a debug session attached to the same AppHost path.
        // Normalize to a no-debug/no-running baseline before validating stale-state clearing.
        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions(120000);
        await stopAppHostIfRunning(appHostPath);
        await waitForNoRunningAppHost(120000, appHostPath);

        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success');
        await waitForRunningAppHost();

        await executeCommandFromPalette('workbench.view.explorer');
        await stopAppHostIfRunning(appHostPath);

        await openAspireView();
        await waitForNoRunningAppHost(120000, appHostPath);
    });

});
