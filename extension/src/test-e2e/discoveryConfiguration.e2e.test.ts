import * as assert from 'assert';
import { spawn, type ChildProcessWithoutNullStreams } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForExtensionState, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { createAdditionalAppHostCandidate, executeE2eControlCommand, removeAdditionalAppHostCandidate, removeLegacyAspireSettings, removeWorkspaceAppHostConfig, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, stopPrimaryAppHostIfRunning, writeLegacyAspireSettings, writeSlowAppHostDiscoveryCliWrapper, writeStreamingAppHostDiscoveryCliWrapper, writeWorkspaceAppHostConfig, writeWorkspaceAppHostConfigRaw, writeWorkspaceCliPath } from './helpers/fixtures';
import { getCliPath, getPrimaryAppHostProjectPath, getRunRoot, getWorkspaceRoot } from './helpers/paths';
import { terminateProcessTree } from './helpers/process';
import { openAspireView, waitForTreeItem, waitForTreeItemDescription, waitForWorkbenchText } from './helpers/vscode';

suite('Aspire workspace discovery and configuration E2E', function () {
    this.timeout(300000);

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

    test('shows running workspace AppHosts while discovery is still pending', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForSelectedWorkspaceAppHost();
        await stopPrimaryAppHostIfRunning();
        await waitForNoRunningAppHost();

        const wrapperPath = writeSlowAppHostDiscoveryCliWrapper(60000);
        await writeWorkspaceCliPath(wrapperPath);

        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
        await waitForExtensionState(
            file => !file.state.isWorkspaceAppHostDiscoveryComplete
                && file.state.workspaceAppHostCandidatePaths.length === 0
                && file.state.appHosts.length === 0,
            'slow workspace discovery to remain pending without AppHost candidates',
            30000);

        const appHostProcessOutput: string[] = [];
        const appHostProcess = startPrimaryAppHost(appHostProcessOutput);
        try {
            await waitForAppHostRunReady(appHostProcessOutput);
            const psRefreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
            await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, psRefreshBefore);

            const appHostPath = getPrimaryAppHostProjectPath();
            const runningDuringDiscovery = await waitForExtensionState(
                file => !file.state.isWorkspaceAppHostDiscoveryComplete
                    && file.state.workspaceAppHostCandidatePaths.length === 0
                    && file.state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath))
                    && file.state.workspaceAppHost !== undefined
                    && isSamePath(file.state.workspaceAppHost.appHostPath, appHostPath)
                    && !file.state.isRepositoryLoading,
                'running workspace AppHost from ps while workspace discovery is pending',
                120000);

            assert.ok(runningDuringDiscovery.state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath)));
        } catch (error) {
            throw new Error(`Running AppHost was not shown before slow discovery completed. AppHost output:\n${appHostProcessOutput.join('')}`, { cause: error });
        } finally {
            await restoreWorkspaceCliPath();
            await terminateAppHostProcess(appHostProcess, appHostProcessOutput);
        }
    });

    test('streams workspace AppHost candidates before discovery completes', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForSelectedWorkspaceAppHost();
        await stopPrimaryAppHostIfRunning();
        await waitForNoRunningAppHost();

        const wrapperPath = writeStreamingAppHostDiscoveryCliWrapper(180000);
        await writeWorkspaceCliPath(wrapperPath);

        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);

        const appHostPath = getPrimaryAppHostProjectPath();
        const streamedCandidate = await waitForExtensionState(
            file => !file.state.isWorkspaceAppHostDiscoveryComplete
                && file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, appHostPath))
                && !file.state.isRepositoryLoading,
            'streamed workspace AppHost candidate before discovery completed',
            60000);
        assert.ok(streamedCandidate.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, appHostPath)));

        let section = await openAspireView();
        const workspaceGroup = await waitForTreeItem(section, 'Workspace AppHosts', 60000);
        await workspaceGroup.expand();
        const idleItem = await waitForTreeItem(section, path.basename(appHostPath), 60000);
        assert.strictEqual(await idleItem.getLabel(), path.basename(appHostPath));

        const appHostProcessOutput: string[] = [];
        const appHostProcess = startPrimaryAppHost(appHostProcessOutput);
        try {
            await waitForAppHostRunReady(appHostProcessOutput);

            const runningDuringDiscovery = await waitForExtensionState(
                file => !file.state.isWorkspaceAppHostDiscoveryComplete
                    && file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, appHostPath))
                    && file.state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath))
                    && file.state.workspaceAppHost !== undefined
                    && isSamePath(file.state.workspaceAppHost.appHostPath, appHostPath)
                    && !file.state.isRepositoryLoading,
                'running workspace AppHost while streamed discovery is pending',
                120000);
            assert.ok(runningDuringDiscovery.state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath)));

            section = await openAspireView();
            const runningItem = await waitForTreeItemDescription(section, path.basename(appHostPath), '(0 resources)', 60000);
            assert.strictEqual(await runningItem.getLabel(), path.basename(appHostPath));
        } catch (error) {
            throw new Error(`Streamed discovery did not show the running AppHost before completion. AppHost output:\n${appHostProcessOutput.join('')}`, { cause: error });
        } finally {
            await restoreWorkspaceCliPath();
            await terminateAppHostProcess(appHostProcess, appHostProcessOutput);
        }
    });
});

function startPrimaryAppHost(output: string[]): ChildProcessWithoutNullStreams {
    const child = spawn(getCliPath(), ['run', '--apphost', getPrimaryAppHostProjectPath(), '--nologo'], {
        cwd: getWorkspaceRoot(),
        env: process.env,
        detached: process.platform !== 'win32',
    });

    child.stdout.on('data', chunk => recordProcessOutput(output, chunk));
    child.stderr.on('data', chunk => recordProcessOutput(output, chunk));

    return child;
}

async function terminateAppHostProcess(child: ChildProcessWithoutNullStreams, output: readonly string[]): Promise<void> {
    const failures: unknown[] = [];

    try {
        if (child.exitCode === null && child.signalCode === null) {
            terminateProcessTree(child.pid, 'SIGTERM');
        }

        await waitForProcessExit(child, 15000);
    } catch (error) {
        failures.push(error);
    }

    try {
        const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
        await executeE2eControlCommand({ name: 'refreshAppHosts' }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
        await waitForNoRunningAppHost(60000);
        await waitForRepositoryIdle(120000);
    } catch (error) {
        failures.push(error);
    }

    if (failures.length > 0) {
        throw new AggregateError(failures, `Failed to clean up AppHost process ${child.pid ?? '<unknown>'}. AppHost output:\n${output.join('')}`);
    }
}

function waitForProcessExit(child: ChildProcessWithoutNullStreams, timeoutMs: number): Promise<void> {
    if (child.exitCode !== null || child.signalCode !== null) {
        return Promise.resolve();
    }

    return new Promise((resolve, reject) => {
        let forceKillTimeout: NodeJS.Timeout | undefined;
        const timeout = setTimeout(() => {
            terminateProcessTree(child.pid, 'SIGKILL');
            forceKillTimeout = setTimeout(() => {
                reject(new Error(`Timed out waiting for AppHost process ${child.pid ?? '<unknown>'} to exit.`));
            }, 5000);
        }, timeoutMs);

        child.once('close', () => {
            clearTimeout(timeout);
            if (forceKillTimeout) {
                clearTimeout(forceKillTimeout);
            }

            resolve();
        });
    });
}

async function waitForAppHostRunReady(output: string[], timeoutMs = 120000): Promise<void> {
    const startedAt = Date.now();
    while (Date.now() - startedAt < timeoutMs) {
        const text = stripAnsi(output.join(''));
        if (text.includes('CTRL+C')) {
            return;
        }

        await delay(250);
    }

    throw new Error(`Timed out waiting for AppHost to start. Output:\n${output.join('')}`);
}

function recordProcessOutput(output: string[], chunk: Buffer): void {
    output.push(chunk.toString());
    while (output.join('').length > 8000) {
        output.shift();
    }
}

function stripAnsi(value: string): string {
    return value.replace(/\x1b\[[0-9;]*m/g, '');
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function getHiddenAppHostDirectory(appHostDirectory: string): string {
    const runRoot = getRunRoot();
    if (runRoot && path.parse(runRoot).root === path.parse(appHostDirectory).root) {
        // The AppHost must move outside the workspace so recursive discovery cannot find it,
        // but staying under the runner root lets the outer E2E cleanup remove it after crashes.
        return path.join(runRoot, '.e2e-hidden-apphost');
    }

    return path.join(path.dirname(getWorkspaceRoot()), `.e2e-hidden-apphost-${process.pid}`);
}
