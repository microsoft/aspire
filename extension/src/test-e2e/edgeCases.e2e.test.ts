import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { AspireExtensionE2EControlCommand } from '../types/extensionApi';
import type { ExecutableLaunchConfiguration } from '../dcp/types';
import { getCommandInvocationCount, getDebugLaunchCount, isSamePath, waitForCommandOutcome, waitForDebugLaunch, waitForDebugSessionStartup, waitForExtensionState, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForRunningAppHost, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { createEmptyAppHostProject, createExternalSingleFileAppHost, executeE2eControlCommand, getGeneratedAppHostPath, getGeneratedProjectRoot, isProcessAlive, removeExternalSingleFileAppHost, removeGeneratedProject, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, waitForKnownProcessExit, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { chooseActiveQuickPick, executeCommandFromPalette, openAspireView, waitForEditorTitle, waitForNotificationMessage } from './helpers/vscode';

interface DebugSessionProcessInfo {
    appHostPath?: string;
    cliPid?: number;
    appHostPid?: number;
}

suite('Aspire extension edge case E2E', function () {
    this.timeout(240000);
    let externalAppHostPath: string | undefined;
    let debuggerInstallHintProjectName: string | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setDebugLaunchSuppressedForE2E(false),
            () => restoreWorkspaceCliPath(),
            () => externalAppHostPath ? stopAppHostIfRunning(externalAppHostPath) : undefined,
            () => debuggerInstallHintProjectName ? stopAppHostIfRunning(getGeneratedAppHostPath(debuggerInstallHintProjectName)) : undefined,
            () => debuggerInstallHintProjectName ? restoreWorkspaceAppHostConfig() : undefined,
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => {
                removeExternalSingleFileAppHost();
                externalAppHostPath = undefined;
            },
            () => {
                if (!debuggerInstallHintProjectName) {
                    return undefined;
                }

                const projectName = debuggerInstallHintProjectName;
                return removeGeneratedProject(projectName).then(() => {
                    debuggerInstallHintProjectName = undefined;
                });
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

        await assert.rejects(
            executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig: {
                    type: 'browser',
                    browser: 'safari',
                    url: 'https://top-secret.invalid',
                } as ExecutableLaunchConfiguration,
            }),
            /E2E control command failed\./);
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

    test('shows debugger install guidance while the Aspire panel and AppHost source are closed', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const debuggerExtensions = await executeE2eControlCommand({ name: 'getResourceDebuggerExtensions' });
        const installedDebuggerTypes = (debuggerExtensions.result as Array<{ resourceType: string }>).map(extension => extension.resourceType);
        assert.ok(!installedDebuggerTypes.includes('python'), 'The clean E2E host must not have the Python debugger extension installed.');

        debuggerInstallHintProjectName = 'DebuggerInstallHintApp';
        const appHostPath = getGeneratedAppHostPath(debuggerInstallHintProjectName);
        await createEmptyAppHostProject(debuggerInstallHintProjectName);

        // This scenario does not exercise dashboard transport security. Use the generated HTTP
        // profile so clean test machines do not need an ambient ASP.NET Core developer certificate.
        const runSettingsPath = path.join(path.dirname(appHostPath), 'apphost.run.json');
        const runSettings = JSON.parse(fs.readFileSync(runSettingsPath, 'utf8')) as { profiles?: Record<string, unknown> };
        assert.ok(runSettings.profiles?.http);
        runSettings.profiles = { http: runSettings.profiles.http };
        writeFileWithRetry(runSettingsPath, JSON.stringify(runSettings, undefined, 2));

        const pythonAppDirectory = path.join(getGeneratedProjectRoot(debuggerInstallHintProjectName), 'pythonapp');
        fs.mkdirSync(pythonAppDirectory, { recursive: true });
        writeFileWithRetry(path.join(pythonAppDirectory, 'app.py'), 'import time\n\nprint("ready", flush=True)\ntime.sleep(600)\n');

        const appHostSource = fs.readFileSync(appHostPath, 'utf8');
        writeFileWithRetry(
            appHostPath,
            appHostSource
                // This scenario validates the Hosting and extension changes together. The local CLI
                // bundle can predate the repo-built packages, which would omit the debugger metadata.
                .replace('#:property AspireUseCliBundle=true', '#:property AspireUseCliBundle=false')
                .replace(
                    'builder.Build().Run();',
                    `#pragma warning disable ASPIREEXTENSION001
builder.AddExecutable("pythonapp", OperatingSystem.IsWindows() ? "python" : "python3", "./pythonapp", "app.py")
    .WithDebugSupport(mode => new { type = "python", mode }, "python");
#pragma warning restore ASPIREEXTENSION001

builder.Build().Run();`));
        writeWorkspaceAppHostConfigForPath(appHostPath);

        await waitForSelectedWorkspaceAppHost(appHostPath);
        await executeE2eControlCommand({ name: 'closeAllEditors' });
        await executeCommandFromPalette('workbench.view.explorer');

        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);

        const notification = await waitForNotificationMessage(
            'Set up Python debugging support to debug resources in this app.',
            60000);
        await notification.dismiss();
    });

    test('process-owner cleanup stops the owned CLI and AppHost process tree', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        const beforeInvocation = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 180000, beforeInvocation);
        await waitForDebugSessionStartup(appHostPath, 180000);
        await waitForRunningAppHost(180000);

        const processInfoStatus = await executeE2eControlCommand({ name: 'getDebugSessionProcessInfo', appHostPath });
        const processInfo = processInfoStatus.result as DebugSessionProcessInfo | undefined;
        assert.ok(processInfo?.cliPid, `Expected the E2E bridge to report the owned Aspire CLI pid: ${JSON.stringify(processInfoStatus)}`);
        assert.ok(processInfo?.appHostPid, `Expected the E2E bridge to report the owned AppHost pid: ${JSON.stringify(processInfoStatus)}`);
        assert.ok(isProcessAlive(processInfo.cliPid), `Expected the Aspire CLI process ${processInfo.cliPid} to be running before deactivation.`);
        assert.ok(isProcessAlive(processInfo.appHostPid), `Expected the AppHost process ${processInfo.appHostPid} to be running before deactivation.`);

        // This exercises the process-owner cleanup methods with real CLI and AppHost processes while
        // keeping the extension host alive. Workbench deactivation is covered separately by unit
        // tests because a reload leaves ExTester unable to complete its own browser shutdown.
        await executeE2eControlCommand({ name: 'stopOwnedDebugSessionProcesses', appHostPath }, { timeoutMs: 30000 });

        await waitForKnownProcessExit(processInfo.cliPid, 'the Aspire CLI process owned by the debug session', 120000);
        await waitForKnownProcessExit(processInfo.appHostPid, 'the AppHost process owned by the debug session', 120000);
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);
    });
});
