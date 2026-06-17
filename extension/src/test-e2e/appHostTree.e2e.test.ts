import * as assert from 'assert';
import { getCommandInvocationCount, getResources, getTerminalCommandCount, getTreeAppHostLabel, isSamePath, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchFailureForE2E, setTerminalCommandExecutionSuppressedForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { cancelActiveInput, clickTreeItem, executeCommandFromPalette, openAspireView, waitForTreeItem } from './helpers/vscode';

suite('Aspire AppHost tree E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => setDebugLaunchFailureForE2E(false),
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
        assert.ok(stateFile.state.workspaceAppHostCandidates.length >= 1);
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

    test('refreshes idle workspace candidate after stop without running manual refresh', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, runBefore);
        await waitForRunningAppHost();

        // Snapshot the latest manual-refresh command sequence before stopping. The automatic
        // refresh on debug-session-end calls dataRepository.refresh() directly and never raises the
        // aspire-vscode.refreshAppHosts command, so this sequence must not advance.
        const refreshSequenceBeforeStop = getCommandInvocationCount('aspire-vscode.refreshAppHosts');

        const stopBefore = getCommandInvocationCount('aspire-vscode.stopAppHost');
        await executeE2eControlCommand({ name: 'stopAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.stopAppHost', 'success', 120000, stopBefore);
        await waitForNoRunningAppHost(120000, appHostPath);

        const stateAfterStop = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidates.some(candidate => isSamePath(candidate.path, appHostPath)),
            'workspace candidate after automatic refresh',
            60000);

        const refreshSequenceAfterStop = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        assert.strictEqual(
            refreshSequenceAfterStop,
            refreshSequenceBeforeStop,
            'No manual refreshAppHosts command should run after stop; the update must come from the automatic debug-session-end refresh.');

        const appHostLabel = getTreeAppHostLabel(stateAfterStop.state);
        const candidate = stateAfterStop.state.workspaceAppHostCandidates.find(candidate => isSamePath(candidate.path, appHostPath));
        assert.ok(candidate);
        const section = await openAspireView();
        const appHostItem = await waitForTreeItem(section, appHostLabel);
        await appHostItem.expand();
        await waitForTreeItem(section, `Status: ${formatStatusLabel(candidate.status)}`);
    });

    test('clears launching state after a failed debug launch', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await setDebugLaunchFailureForE2E(true);
        try {
            const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
            await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.runAppHost', 'error', 60000, runBefore);
            await waitForNoRunningAppHost(120000, appHostPath);

            await waitForExtensionState(
                file => file.state.workspaceAppHostCandidates.some(candidate => isSamePath(candidate.path, appHostPath)),
                'workspace candidate after failed debug launch',
                60000);
        } finally {
            await setDebugLaunchFailureForE2E(false);
        }
    });
});

function formatStatusLabel(status: string): string {
    return status
        .split(/[-_\s]+/)
        .filter(part => part.length > 0)
        .map(part => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
        .join(' ');
}
