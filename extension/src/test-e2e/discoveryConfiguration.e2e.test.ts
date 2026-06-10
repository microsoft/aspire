import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForExtensionState, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { createAdditionalAppHostCandidate, executeE2eControlCommand, removeAdditionalAppHostCandidate, removeLegacyAspireSettings, removeWorkspaceAppHostConfig, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, stopPrimaryAppHostIfRunning, writeLegacyAspireSettings, writeWorkspaceAppHostConfig, writeWorkspaceAppHostConfigRaw } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getRunRoot, getWorkspaceRoot } from './helpers/paths';
import { openAspireView, waitForWorkbenchText } from './helpers/vscode';

suite('Aspire workspace discovery and configuration E2E', function () {
    this.timeout(180000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => restoreWorkspaceAppHostConfig(),
            () => removeLegacyAspireSettings(),
            () => removeAdditionalAppHostCandidate(),
            () => stopPrimaryAppHostIfRunning(),
        ], 'Discovery configuration E2E teardown failed.');
    });

    test('rediscovers workspace AppHost candidates when config changes', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        removeWorkspaceAppHostConfig();
        const refreshWithoutConfigBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshWithoutConfigBefore);

        const primaryCandidate = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, getPrimaryAppHostProjectPath())) && !file.state.hasError,
            'primary AppHost candidate after removing aspire.config.json',
            60000);
        assert.ok(primaryCandidate.state.workspaceAppHostCandidatePaths.length >= 1);

        const secondaryAppHostPath = createAdditionalAppHostCandidate();
        const refreshWithSecondCandidateBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshWithSecondCandidateBefore);

        const multipleCandidates = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, secondaryAppHostPath)),
            'secondary AppHost candidate',
            60000);
        assert.ok(multipleCandidates.state.workspaceAppHostCandidatePaths.length >= 2);

        restoreWorkspaceAppHostConfig();
        removeAdditionalAppHostCandidate();
        const refreshRestoredConfigBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshRestoredConfigBefore);

        const restored = await waitForWorkspaceAppHost();
        assert.ok(restored.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, getPrimaryAppHostProjectPath())));
    });

    test('handles malformed, JSONC, absolute, and legacy AppHost configuration files', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForSelectedWorkspaceAppHost();

        writeWorkspaceAppHostConfigRaw(`{
  // The JSON language service should report this, but discovery must fall back to the CLI candidate.
  "appHost": { "path":
`);
        let before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);
        const malformedFallback = await waitForExtensionState(
            file => file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, getPrimaryAppHostProjectPath())),
            'CLI-discovered AppHost after malformed aspire.config.json',
            60000);
        assert.ok(malformedFallback.state.workspaceAppHostCandidatePaths.length >= 1);

        writeWorkspaceAppHostConfigRaw(`{
  // JSONC comments are supported by the shared config parser.
  "appHost": {
    "path": "AspireE2E.AppHost/AspireE2E.AppHost.csproj"
  }
}`);
        before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);
        await waitForSelectedWorkspaceAppHost();

        writeWorkspaceAppHostConfig({ appHost: { path: getPrimaryAppHostProjectPath() } });
        before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);
        await waitForSelectedWorkspaceAppHost();

        removeWorkspaceAppHostConfig();
        writeLegacyAspireSettings();
        before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);
        await waitForSelectedWorkspaceAppHost();

        const secondaryAppHostPath = createAdditionalAppHostCandidate();
        writeLegacyAspireSettings(path.join('..', 'AspireE2E.SecondAppHost', 'AspireE2E.SecondAppHost.csproj'));
        restoreWorkspaceAppHostConfig();
        before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);
        const selected = await waitForSelectedWorkspaceAppHost();
        assert.ok(!isSamePath(selected.state.workspaceAppHostPath ?? '', secondaryAppHostPath));
    });

    test('shows the empty workspace welcome after discovery finds no AppHosts', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForSelectedWorkspaceAppHost();
        await stopPrimaryAppHostIfRunning();

        const appHostDirectory = path.dirname(getPrimaryAppHostProjectPath());
        const hiddenAppHostDirectory = getHiddenAppHostDirectory(appHostDirectory);
        fs.rmSync(hiddenAppHostDirectory, { recursive: true, force: true });

        const failures: unknown[] = [];
        try {
            fs.renameSync(appHostDirectory, hiddenAppHostDirectory);
            removeWorkspaceAppHostConfig();

            const before = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
            await executeE2eControlCommand({ name: 'refreshAppHosts' });
            await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, before);

            const emptyWorkspace = await waitForExtensionState(
                file => file.state.isWorkspaceAppHostDiscoveryComplete
                    && !file.state.isRepositoryLoading
                    && file.state.workspaceAppHostCandidatePaths.length === 0
                    && file.state.workspaceResources.length === 0
                    && file.state.appHosts.length === 0
                    && !file.state.hasError,
                'empty workspace discovery to complete without loading forever',
                60000);
            assert.deepStrictEqual(emptyWorkspace.state.workspaceAppHostCandidatePaths, []);

            await waitForWorkbenchText('No Aspire AppHosts detected in this workspace.', 30000);
        } catch (error) {
            failures.push(error);
        } finally {
            let appHostRestored = fs.existsSync(appHostDirectory);
            if (fs.existsSync(hiddenAppHostDirectory) && !fs.existsSync(appHostDirectory)) {
                try {
                    fs.renameSync(hiddenAppHostDirectory, appHostDirectory);
                    appHostRestored = true;
                } catch (error) {
                    failures.push(error);
                }
            }

            if (appHostRestored) {
                try {
                    fs.rmSync(hiddenAppHostDirectory, { recursive: true, force: true });
                } catch (error) {
                    failures.push(error);
                }
            }

            try {
                restoreWorkspaceAppHostConfig();
            } catch (error) {
                failures.push(error);
            }

            if (failures.length > 0) {
                throw new AggregateError(failures, 'Discovery configuration E2E test or cleanup failed.');
            }
        }
    });
});

function getHiddenAppHostDirectory(appHostDirectory: string): string {
    const runRoot = getRunRoot();
    if (runRoot && path.parse(runRoot).root === path.parse(appHostDirectory).root) {
        // The AppHost must move outside the workspace so recursive discovery cannot find it,
        // but staying under the runner root lets the outer E2E cleanup remove it after crashes.
        return path.join(runRoot, '.e2e-hidden-apphost');
    }

    return path.join(path.dirname(getWorkspaceRoot()), `.e2e-hidden-apphost-${process.pid}`);
}
