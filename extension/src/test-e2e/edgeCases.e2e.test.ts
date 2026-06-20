import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { AspireExtensionE2EControlCommand } from '../types/extensionApi';
import { getCommandInvocationCount, getDebugLaunchCount, isSamePath, waitForCommandOutcome, waitForDebugLaunch, waitForExtensionState, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire extension edge case E2E', function () {
    this.timeout(180000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setDebugLaunchSuppressedForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => stopPrimaryAppHostIfRunning(),
        ], 'Edge case E2E teardown failed.');
    });

    test('rejects invalid E2E control payloads and missing tree targets without side effects', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        await assert.rejects(
            executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'workbench.action.reloadWindow' } as unknown as AspireExtensionE2EControlCommand),
            /requires an aspire-vscode command id/);

        await assert.rejects(
            executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.settings', args: 'not-an-array' } as unknown as AspireExtensionE2EControlCommand),
            /args must be an array/);

        await assert.rejects(
            executeE2eControlCommand({ name: 'copyResourceName', resourceName: 'missing-resource' }),
            /could not find resource 'missing-resource'/);

        await assert.rejects(
            executeE2eControlCommand({ name: 'copyEndpointUrl', url: 'http://127.0.0.1:1/not-a-resource-endpoint' }),
            /could not find a matching endpoint/);

        await assert.rejects(
            executeE2eControlCommand({ name: 'viewAppHostLogFile', appHostPath: getPrimaryAppHostProjectPath() }),
            /could not find an AppHost log file/);

        const beforePublishLaunch = getDebugLaunchCount();
        await assert.rejects(
            executeE2eControlCommand({ name: 'publishAppHost' }),
            /publishAppHost requires appHostPath/);
        assert.strictEqual(getDebugLaunchCount(), beforePublishLaunch);
    });

    test('keeps CLI-independent settings commands available when the CLI is unavailable', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await setCliUnavailableForE2E(true);

        const settingsBefore = getCommandInvocationCount('aspire-vscode.settings');
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.settings' });
        await waitForCommandOutcome('aspire-vscode.settings', 'success', 60000, settingsBefore);

        const configureBefore = getCommandInvocationCount('aspire-vscode.configureLaunchJson');
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.configureLaunchJson' });
        await waitForCommandOutcome('aspire-vscode.configureLaunchJson', 'success', 60000, configureBefore);

        const launchJsonPath = path.join(getWorkspaceRoot(), '.vscode', 'launch.json');
        const launchJson = JSON.parse(fs.readFileSync(launchJsonPath, 'utf8')) as { configurations?: Array<{ type?: string }> };
        assert.ok(launchJson.configurations?.some(configuration => configuration.type === 'aspire'));
    });

    test('clears launch state after suppressed debug launch requests', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await setDebugLaunchSuppressedForE2E(true);

        const appHostPath = getPrimaryAppHostProjectPath();
        const beforeInvocation = getCommandInvocationCount('aspire-vscode.debugAppHost');
        const beforeLaunch = getDebugLaunchCount();
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeInvocation);

        const launch = await waitForDebugLaunch(
            event => event.executionSuppressed && event.command === 'run' && !event.noDebug,
            'suppressed debug AppHost launch',
            60000,
            beforeLaunch);
        assert.ok(isSamePath(launch.appHostPath, appHostPath));

        await waitForExtensionState(
            file => !file.state.launchingPaths.some(launchingPath => isSamePath(launchingPath, appHostPath)),
            'suppressed debug launch state to clear',
            60000);
    });
});
