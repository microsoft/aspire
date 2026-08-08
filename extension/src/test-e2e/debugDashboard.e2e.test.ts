import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getDebugLaunchCount, getStoppingPathEventCount, getTreeAppHostLabel, isSamePath, waitForAppHostLaunching, waitForCommandOutcome, waitForDebugConsoleOutput, waitForDebugDashboardUrl, waitForDebugLaunch, waitForDebugSessionStartup, waitForExtensionState, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForRunningAppHost, waitForStoppingPathEvent, waitForWorkspaceAppHost } from './helpers/assertions';
import { createAdditionalAppHostCandidate, executeE2eControlCommand, removeAdditionalAppHostCandidate, resetDashboardDefaultChangedNotificationForE2E, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setShowStatusDelayForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, writeFileWithRetry, writeWorkspaceSetting } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { openAspireView, waitForEditorTitle, waitForNotificationMessage, waitForTreeItem, waitForWorkbenchTextAfterIntegratedBrowserNavigation } from './helpers/vscode';

suite('Aspire debug dashboard E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setShowStatusDelayForE2E(undefined),
            () => resetDashboardDefaultChangedNotificationForE2E(),
            () => writeWorkspaceSetting('aspire.dashboardBrowser', undefined),
            () => writeWorkspaceSetting('aspire.enableAspireDashboardAutoLaunch', undefined),
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
            () => removeAdditionalAppHostCandidate(),
        ], 'Debug dashboard E2E teardown failed.');
    });

    test('debugs the AppHost with unconfigured dashboard launch defaults', async () => {
        await openAspireView();
        await resetDashboardDefaultChangedNotificationForE2E();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);

        await waitForDebugSessionStartup();
        await waitForDebugConsoleOutput('Dashboard:', appHostPath, 120000);
        const dashboardUrl = await waitForDashboardLoginUrl(appHostPath);
        // Probing the origin proves the dashboard endpoint is available without trying to
        // authenticate through the one-time login URL. A 200 from the dashboard host is enough
        // for this regression: the unconfigured default must not auto-open, but the dashboard
        // URL must still be emitted for the panel/debug console path.
        await waitForHttpText(new URL(dashboardUrl).origin, 'Aspire', 120000);
        await waitForNotificationMessage('The Aspire Dashboard does not open automatically', 60000);

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();
    });

    test('debugs the AppHost and opens the dashboard in the integrated browser', async () => {
        writeWorkspaceSetting('aspire.dashboardBrowser', 'integratedBrowser');

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostLabel = getTreeAppHostLabel(discovered.state);
        const section = await openAspireView();

        const idleItem = await waitForTreeItem(section, appHostLabel);
        await idleItem.expand();
        await waitForTreeItem(section, 'Debug AppHost');
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForAppHostLaunching(appHostPath);
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);

        await waitForDebugSessionStartup();
        const dashboard = await waitForDebugDashboardUrl();
        const dashboardUrl = dashboard.state.debugSessions.find(session => session.dashboardUrl?.startsWith('http'))?.dashboardUrl;
        assert.ok(dashboardUrl);

        await waitForHttpText(dashboardUrl, 'Aspire', 120000, new URL(dashboardUrl).origin);
        if (process.platform === 'win32') {
            // Chromium webview text extraction is unreliable on hosted Windows runners after
            // integrated-browser navigation. The HTTP probe above proves the dashboard rendered
            // content, and Windows keeps the editor-title assertion as a weaker UI check.
            assert.ok((await waitForEditorTitle(new URL(dashboardUrl).host, 120000, { matchCase: false })).toLowerCase().includes(new URL(dashboardUrl).host.toLowerCase()));
        }
        else {
            const dashboardHost = new URL(dashboardUrl).host;
            const browserText = await waitForWorkbenchTextAfterIntegratedBrowserNavigation(['Resources', dashboardHost]);
            assert.ok(browserText.includes('Resources') || browserText.includes(dashboardHost));
        }

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();
    });

    test('workspace debug stop removes running apphost', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });

        const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);
        await waitForDebugSessionStartup(appHostPath);
        await waitForRunningAppHost();

        await setShowStatusDelayForE2E(2500);
        try {
            const beforeStoppingPathEvent = getStoppingPathEventCount();
            await executeE2eControlCommand({ name: 'stopDebugging' }, { waitFor: 'started' });
            await waitForStoppingPathEvent(appHostPath, 'entered', beforeStoppingPathEvent, 120000);
            await waitForNoDebugSessions();
            await waitForNoRunningAppHost(120000, appHostPath);
            await waitForExtensionState(
                file => !file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath)),
                `AppHost '${appHostPath}' to leave stopping state`,
                120000);
        } finally {
            await setShowStatusDelayForE2E(undefined);
        }
    });

    test('global debug stop removes running apphost', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await executeE2eControlCommand({ name: 'switchToGlobalView' });

        const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);
        await waitForDebugSessionStartup(appHostPath);
        await waitForRunningAppHost();

        await setShowStatusDelayForE2E(2500);
        try {
            const beforeStoppingPathEvent = getStoppingPathEventCount();
            await executeE2eControlCommand({ name: 'stopDebugging' }, { waitFor: 'started' });
            await waitForStoppingPathEvent(appHostPath, 'entered', beforeStoppingPathEvent, 120000);
            await waitForNoDebugSessions();
            await waitForNoRunningAppHost(120000, appHostPath);
            await waitForExtensionState(
                file => !file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath)),
                `AppHost '${appHostPath}' to leave stopping state`,
                120000);
        } finally {
            await setShowStatusDelayForE2E(undefined);
        }
    });

    test('publish session completion does not mark a running AppHost as stopping', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        await executeE2eControlCommand({ name: 'switchToWorkspaceView' });

        const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);
        await waitForDebugSessionStartup(appHostPath);
        await waitForRunningAppHost();

        const beforeDebugLaunch = getDebugLaunchCount();
        await setShowStatusDelayForE2E(2500);
        try {
            await executeE2eControlCommand({ name: 'publishAppHost', appHostPath }, { waitFor: 'started', timeoutMs: 30000 });
            await waitForDebugLaunch(
                event => event.command === 'publish' && event.appHostPath !== undefined && isSamePath(event.appHostPath, appHostPath),
                `publish launch for AppHost '${appHostPath}'`,
                30000,
                beforeDebugLaunch);
            await waitForDebugConsoleOutput('publish completed successfully', appHostPath, 120000);
            await waitForExtensionState(
                file =>
                    file.state.debugSessions.length === 1 &&
                    file.state.debugSessions.some(session => session.appHostPath !== undefined && isSamePath(session.appHostPath, appHostPath) && session.startupCompleted) &&
                    !file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath)),
                `AppHost '${appHostPath}' to remain running without entering stopping state after publish`,
                30000);
        } finally {
            await setShowStatusDelayForE2E(undefined);
        }

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();
        await waitForNoRunningAppHost(120000, appHostPath);
    });

    test('switching named launch configurations does not change the workspace AppHost default', async function () {
        if (process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true') {
            return;
        }

        // Each iteration starts and stops a full AppHost debug session, and the secondary AppHost is
        // created at test time so its first build is cold. That does not fit the suite-wide budget.
        this.timeout(600000);

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const workspaceRoot = getWorkspaceRoot();
        const primaryAppHostPath = getPrimaryAppHostProjectPath();
        const configPath = path.join(workspaceRoot, 'aspire.config.json');
        const launchJsonPath = path.join(workspaceRoot, '.vscode', 'launch.json');
        let secondaryAppHostPath: string | undefined;
        let originalConfig: Buffer | undefined;
        let originalLaunchJson: Buffer | undefined;

        try {
            secondaryAppHostPath = createAdditionalAppHostCandidate();
            originalConfig = fs.readFileSync(configPath);
            originalLaunchJson = fs.existsSync(launchJsonPath) ? fs.readFileSync(launchJsonPath) : undefined;
            const configurations = [
                { name: 'Primary AppHost', program: primaryAppHostPath },
                { name: 'Secondary AppHost', program: secondaryAppHostPath },
            ];

            writeFileWithRetry(launchJsonPath, JSON.stringify({
                version: '0.2.0',
                configurations: configurations.map(configuration => ({
                    type: 'aspire',
                    request: 'launch',
                    ...configuration,
                })),
            }, undefined, 2));

            // VS Code refreshes its launch-configuration cache from a file watcher; the
            // startDebugConfiguration control command waits for the entry to become resolvable.
            for (const configuration of configurations) {
                await executeE2eControlCommand(
                    { name: 'startDebugConfiguration', configurationName: configuration.name },
                    { waitFor: 'started' });
                await waitForDebugSessionStartup(configuration.program);

                assert.deepStrictEqual(
                    fs.readFileSync(configPath),
                    originalConfig,
                    `Expected ${configuration.name} to leave aspire.config.json byte-for-byte unchanged.`);

                await executeE2eControlCommand({ name: 'stopDebugging' });
                await waitForNoDebugSessions();
                await waitForNoRunningAppHost(120000, configuration.program);
            }
        }
        finally {
            const capturedConfig = originalConfig;
            const capturedLaunchJson = originalLaunchJson;
            const capturedSecondaryAppHostPath = secondaryAppHostPath;
            await runE2eTeardown([
                () => executeE2eControlCommand({ name: 'stopDebugging' }),
                () => waitForNoDebugSessions().catch(() => undefined),
                () => stopAppHostIfRunning(primaryAppHostPath),
                () => capturedSecondaryAppHostPath ? stopAppHostIfRunning(capturedSecondaryAppHostPath) : undefined,
                () => capturedConfig === undefined ? undefined : fs.writeFileSync(configPath, capturedConfig),
                () => capturedLaunchJson === undefined
                    ? fs.rmSync(launchJsonPath, { force: true })
                    : fs.writeFileSync(launchJsonPath, capturedLaunchJson),
                () => removeAdditionalAppHostCandidate(),
            ], 'Named launch configuration persistence E2E cleanup failed.');
        }
    });

    test('surfaces AppHost build failure logs in the debug console when the CLI exits after a build failure', async function () {
        if (process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true') {
            return;
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const appHostSourcePath = path.join(path.dirname(appHostPath), 'AppHost.cs');
        const originalSource = fs.readFileSync(appHostSourcePath, 'utf8');

        try {
            const brokenSource = originalSource.replace(
                'builder.Build().Run();',
                '__AspireE2EFlushRegressionMissingSymbol__();\n\nbuilder.Build().Run();');
            assert.notStrictEqual(brokenSource, originalSource, 'Expected AppHost fixture to contain builder.Build().Run().');
            writeFileWithRetry(appHostSourcePath, brokenSource);
            await setShowStatusDelayForE2E(2500);

            const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
            await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);
            await waitForDebugConsoleOutput("__AspireE2EFlushRegressionMissingSymbol__' does not exist", appHostPath, 120000);
            await waitForDebugConsoleOutput('The project could not be built', appHostPath, 120000);
            const logOutput = await waitForDebugConsoleOutput('See logs at', appHostPath, 120000);
            assert.ok(!logOutput.output.includes('\u001b]8;'), `Expected debug console log output to omit terminal hyperlinks: ${JSON.stringify(logOutput.output)}`);
        }
        finally {
            await runE2eTeardown([
                () => setShowStatusDelayForE2E(undefined),
                () => writeFileWithRetry(appHostSourcePath, originalSource),
                () => executeE2eControlCommand({ name: 'stopDebugging' }),
                () => waitForNoDebugSessions().catch(() => undefined),
            ], 'Debug dashboard build failure cleanup failed.');
        }
    });
});

async function waitForDashboardLoginUrl(appHostPath: string): Promise<string> {
    const loginOutput = await waitForDebugConsoleOutput('/login?t=', appHostPath, 120000);
    const match = loginOutput.output.match(/https?:\/\/\S*\/login\?t=[^\s\u001b]+/);
    assert.ok(match, `Expected dashboard login URL in output: ${loginOutput.output}`);

    return match[0];
}
