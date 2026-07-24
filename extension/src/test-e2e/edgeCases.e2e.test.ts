import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { AspireExtensionE2EControlCommand } from '../types/extensionApi';
import { getCommandInvocationCount, getDebugLaunchCount, isSamePath, waitForCommandOutcome, waitForDebugLaunch, waitForExtensionState, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { createExternalSingleFileAppHost, executeE2eControlCommand, removeExternalSingleFileAppHost, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { chooseActiveQuickPick, executeCommandFromPalette, openAspireView, waitForEditorTitle } from './helpers/vscode';

suite('Aspire extension edge case E2E', function () {
    this.timeout(240000);
    let externalAppHostPath: string | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setDebugLaunchSuppressedForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => externalAppHostPath ? stopAppHostIfRunning(externalAppHostPath) : undefined,
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => {
                removeExternalSingleFileAppHost();
                externalAppHostPath = undefined;
            },
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

        const launchJsonPath = path.join(getWorkspaceRoot(), '.vscode', 'launch.json');
        fs.rmSync(launchJsonPath, { force: true });

        const configureBefore = getCommandInvocationCount('aspire-vscode.configureLaunchJson');
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.configureLaunchJson' }, { waitFor: 'started' });
        await chooseActiveQuickPick('Do not open the dashboard');
        await waitForCommandOutcome('aspire-vscode.configureLaunchJson', 'success', 60000, configureBefore);

        const launchJson = JSON.parse(fs.readFileSync(launchJsonPath, 'utf8')) as { configurations?: Array<{ type?: string; dashboardBrowser?: string }> };
        assert.ok(launchJson.configurations?.some(configuration => configuration.type === 'aspire' && configuration.dashboardBrowser === 'none'));
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

    test('keeps an external single-file AppHost followed while its tab is backgrounded', async () => {
        const appHostPath = createExternalSingleFileAppHost();
        externalAppHostPath = appHostPath;
        await openAspireView();
        await waitForRepositoryIdle();

        await executeE2eControlCommand({ name: 'openFile', filePath: appHostPath });
        await waitForEditorTitle('apphost.cs');

        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);
        await waitForExtensionState(file =>
            file.state.appHosts.some(appHost =>
                isSamePath(appHost.appHostPath, appHostPath)
                && (appHost.resources ?? []).some(resource => resource.name === 'external-value')),
        'external AppHost resources to be followed while its tab is active',
        180000);

        const workspaceAppHostPath = getPrimaryAppHostProjectPath();
        await executeE2eControlCommand({ name: 'openFile', filePath: workspaceAppHostPath });
        const activeEditor = await executeE2eControlCommand({ name: 'getActiveEditor' });
        const activeEditorPath = (activeEditor.result as { fileName?: string }).fileName;
        assert.ok(activeEditorPath && isSamePath(activeEditorPath, workspaceAppHostPath));
        await waitForEditorTitle('apphost.cs');

        await executeCommandFromPalette('workbench.view.explorer');
        const backgrounded = await waitForExtensionState(file =>
            file.state.appHosts.some(appHost =>
                isSamePath(appHost.appHostPath, appHostPath)
                && (appHost.resources ?? []).some(resource => resource.name === 'external-value')),
        'external AppHost and resources to remain followed after its tab is backgrounded and the Aspire panel is hidden',
        60000);
        assert.ok(backgrounded.state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath)));
    });
});
