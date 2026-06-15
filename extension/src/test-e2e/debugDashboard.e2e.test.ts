import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getTreeAppHostLabel, waitForAppHostLaunching, waitForCommandOutcome, waitForDebugConsoleOutput, waitForDebugDashboardUrl, waitForDebugSessionStartup, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, setCliUnavailableForE2E, setShowStatusDelayForE2E, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
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

    test('keeps AppHost build diagnostics in the debug console when the CLI exits after a build failure', async function () {
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
            fs.writeFileSync(appHostSourcePath, brokenSource);
            await setShowStatusDelayForE2E(2500);

            const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
            await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);
            await waitForDebugConsoleOutput('__AspireE2EFlushRegressionMissingSymbol__', appHostPath, 120000);
            const logOutput = await waitForDebugConsoleOutput('See logs at', appHostPath, 120000);
            assert.ok(!logOutput.output.includes('\u001b]8;'), `Expected debug console log output to omit terminal hyperlinks: ${JSON.stringify(logOutput.output)}`);
        }
        finally {
            await setShowStatusDelayForE2E(undefined);
            fs.writeFileSync(appHostSourcePath, originalSource);
            await executeE2eControlCommand({ name: 'stopDebugging' });
            await waitForNoDebugSessions().catch(() => undefined);
        }
    });
});
