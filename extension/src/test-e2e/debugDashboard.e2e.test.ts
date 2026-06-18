import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getDebugLaunchCount, getTreeAppHostLabel, isSamePath, waitForAppHostLaunching, waitForCommandOutcome, waitForDebugConsoleOutput, waitForDebugDashboardUrl, waitForDebugLaunch, waitForDebugSessionStartup, waitForExtensionState, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForRunningAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setShowStatusDelayForE2E, stopPrimaryAppHostIfRunning, writeFileWithRetry } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView, waitForEditorTitle, waitForTreeItem, waitForWorkbenchTextAfterIntegratedBrowserNavigation } from './helpers/vscode';

suite('Aspire debug dashboard E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => setCliUnavailableForE2E(false),
            () => setShowStatusDelayForE2E(undefined),
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'Debug dashboard E2E teardown failed.');
    });

    test('debugs the AppHost and opens the dashboard in the integrated browser', async () => {
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
            await executeE2eControlCommand({ name: 'stopDebugging' });
            await waitForExtensionState(
                file => file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath)),
                `AppHost '${appHostPath}' to enter stopping state`,
                120000);
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
            await executeE2eControlCommand({ name: 'stopDebugging' });
            await waitForExtensionState(
                file => file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath)),
                `AppHost '${appHostPath}' to enter stopping state`,
                120000);
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
