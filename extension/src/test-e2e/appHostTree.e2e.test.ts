import * as assert from 'assert';
import { getCommandInvocationCount, getResources, getTerminalCommandCount, getTreeAppHostLabel, isSamePath, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreE2eCliPathForE2E, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setE2eCliPathForE2E, setTerminalCommandExecutionSuppressedForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, writeStreamingDiscoveryCliWrapper } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { cancelActiveInput, clickTreeItem, executeCommandFromPalette, openAspireView, waitForTreeItem, waitForWorkbenchText } from './helpers/vscode';

suite('Aspire AppHost tree E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
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

        await setE2eCliPathForE2E(writeStreamingDiscoveryCliWrapper());
        const invocationCountBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });

        const loadingState = await waitForExtensionState(
            file => file.state.isRepositoryLoading
                && file.state.isWorkspaceAppHostDiscoveryComplete === false
                && file.state.workspaceAppHostPath === undefined
                && file.state.workspaceAppHostCandidatePaths.length === 0,
            'workspace AppHost refresh loading state',
            30000);
        assert.strictEqual(loadingState.state.isRepositoryLoading, true);
        const loadingText = await waitForWorkbenchText('Searching for AppHosts...', 30000);
        assert.ok(!loadingText.includes('No Aspire AppHosts detected in this workspace.'));

        const partialState = await waitForExtensionState(
            file => file.state.isWorkspaceAppHostDiscoveryComplete === false &&
                file.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath())),
            'streamed AppHost candidate before discovery completes',
            30000);
        assert.strictEqual(partialState.state.isWorkspaceAppHostDiscoveryComplete, false);

        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 30000, invocationCountBefore);
        const finalState = await waitForRepositoryIdle();
        assert.strictEqual(finalState.state.isWorkspaceAppHostDiscoveryComplete, true);
        assert.ok(finalState.state.workspaceAppHostCandidatePaths.some(candidatePath => isSamePath(candidatePath, getPrimaryAppHostProjectPath())));
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
