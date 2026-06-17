import * as assert from 'assert';
import { getResources, getTerminalCommandCount, getTreeAppHostLabel, waitForCommandOutcome, waitForDashboardUrl, waitForExtensionState, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResource, waitForRunningAppHost, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchFailureForE2E, setE2eCliPathForE2E, setTerminalCommandExecutionSuppressedForE2E, stopPrimaryAppHostIfRunning, writeLsSequenceCliWrapper } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { cancelActiveInput, clickTreeItem, openAspireView, waitForTreeItem } from './helpers/vscode';

suite('Aspire AppHost tree E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setTerminalCommandExecutionSuppressedForE2E(false),
            () => setDebugLaunchFailureForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => stopPrimaryAppHostIfRunning(),
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

    test('promotes a possibly-buildable idle candidate to buildable after stop', async () => {
        const appHostPath = getPrimaryAppHostProjectPath();
        const wrapperPath = writeLsSequenceCliWrapper([
            [{ path: appHostPath, language: 'csharp', status: 'possibly-buildable', selected: true }],
            [{ path: appHostPath, language: 'csharp', status: 'buildable', selected: true }],
        ], 'aspire-ls-sequence-auto-buildable');
        await setE2eCliPathForE2E(wrapperPath);

        await openAspireView();
        await waitForRepositoryIdle();
        const refreshInvocationBefore = await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000).then(event => event.sequence).catch(() => 0);
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshInvocationBefore);

        await waitForExtensionState(
            file => file.state.workspaceAppHostCandidates.some(candidate => candidate.path === appHostPath && candidate.status === 'possibly-buildable'),
            'possibly-buildable workspace candidate',
            60000);

        const runBefore = await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 1000).then(event => event.sequence).catch(() => 0);
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, runBefore);
        await waitForRunningAppHost();

        // Snapshot the latest manual-refresh command sequence before stopping. The automatic
        // refresh on debug-session-end calls dataRepository.refresh() directly and never raises the
        // aspire-vscode.refreshAppHosts command, so this sequence must not advance.
        const refreshSequenceBeforeStop = await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 1000).then(event => event.sequence).catch(() => 0);

        const stopBefore = await waitForCommandOutcome('aspire-vscode.stopAppHost', 'success', 1000).then(event => event.sequence).catch(() => 0);
        await executeE2eControlCommand({ name: 'stopAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.stopAppHost', 'success', 120000, stopBefore);
        await waitForNoRunningAppHost();

        const stateWithBuildableCandidate = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidates.some(candidate => candidate.path === appHostPath && candidate.status === 'buildable'),
            'buildable workspace candidate after automatic refresh',
            60000);

        const refreshSequenceAfterStop = await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 1000).then(event => event.sequence).catch(() => 0);
        assert.strictEqual(
            refreshSequenceAfterStop,
            refreshSequenceBeforeStop,
            'No manual refreshAppHosts command should run after stop; promotion must come from the automatic debug-session-end refresh.');

        const appHostLabel = getTreeAppHostLabel(stateWithBuildableCandidate.state);
        const section = await openAspireView();
        const appHostItem = await waitForTreeItem(section, appHostLabel);
        await appHostItem.expand();
        await waitForTreeItem(section, 'Status: Buildable');
    });

    test('removes idle workspace candidate after a failed build', async () => {
        const appHostPath = getPrimaryAppHostProjectPath();
        const wrapperPath = writeLsSequenceCliWrapper([
            [{ path: appHostPath, language: 'csharp', status: 'possibly-buildable', selected: true }],
            [{ path: appHostPath, language: 'csharp', status: 'possibly-unbuildable', selected: true }],
        ], 'aspire-ls-sequence-failed-build');
        await setE2eCliPathForE2E(wrapperPath);

        await openAspireView();
        await waitForRepositoryIdle();
        const refreshInvocationBefore = await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000).then(event => event.sequence).catch(() => 0);
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshInvocationBefore);

        await waitForExtensionState(
            file => file.state.workspaceAppHostCandidates.some(candidate => candidate.path === appHostPath && candidate.status === 'possibly-buildable'),
            'possibly-buildable workspace candidate',
            60000);

        await setDebugLaunchFailureForE2E(true);
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });

        await waitForExtensionState(
            file => file.state.workspaceAppHostCandidates.every(candidate => candidate.path !== appHostPath),
            'workspace candidate removed after failed build',
            60000);
    });
});
