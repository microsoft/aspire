import * as assert from 'assert';
import { findRunningAppHost, getCommandInvocationCount, getResources, getTerminalCommandCount, getTreeAppHostLabel, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setTerminalCommandExecutionSuppressedForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { cancelActiveInput, clickTreeItem, executeCommandFromPalette, openAspireView, waitForTreeItem } from './helpers/vscode';

suite('Aspire AppHost tree E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'switchToWorkspaceView' }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'AppHost tree E2E teardown failed.');
    });

    function hasWorkerResource(resources: readonly { name: string; displayName: string | null }[] | null | undefined): boolean {
        return (resources ?? []).some(resource => (resource.displayName ?? resource.name) === 'e2e-worker');
    }

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

    test('surfaces the same describe resources in workspace and global views from one stream', async () => {
        const appHostPath = getPrimaryAppHostProjectPath();

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForRunningAppHost();

        await waitForExtensionState(
            file => hasWorkerResource(file.state.workspaceResources),
            'e2e-worker in workspaceResources projection (workspace view)',
            120000);

        // Switching view must not respawn the stream; the same stream's resources now attach onto
        // appHosts[].resources instead of the workspace projection.
        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        const globalState = await waitForExtensionState(
            file => hasWorkerResource(findRunningAppHost(file.state, appHostPath)?.resources),
            'e2e-worker attached to appHosts[].resources (global view)',
            120000);
        assert.ok(!hasWorkerResource(globalState.state.workspaceResources),
            'Global view must not also project the host into workspaceResources (would double-render).');

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });
        await waitForExtensionState(
            file => hasWorkerResource(file.state.workspaceResources),
            'e2e-worker projection restored after returning to workspace view',
            120000);
    });

    test('streams describe resources for a host run while global view is active', async () => {
        const appHostPath = getPrimaryAppHostProjectPath();

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        // Run the host with global view already active so the resources can only come from the global
        // attach path, never the workspace projection.
        await executeE2eControlCommand({ name: 'switchToGlobalView' });
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForRunningAppHost();

        const state = await waitForExtensionState(
            file => hasWorkerResource(findRunningAppHost(file.state, appHostPath)?.resources),
            'e2e-worker attached to appHosts[].resources for a host run in global view',
            120000);
        assert.ok(!hasWorkerResource(state.state.workspaceResources),
            'Workspace projection must stay empty while in global view.');
    });

});
