import * as assert from 'assert';
import { spawn, spawnSync, type ChildProcessWithoutNullStreams } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { findRunningAppHost, getCommandInvocationCount, getDebugLaunchCount, isSamePath, readStateFile, waitForCommandOutcome, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, getCliWrapperInvocations, restoreE2eCliPathForE2E, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, runE2eTeardown, setE2eCliPathForE2E, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, writeTokenlessStableCliWrapper, writeWorkspaceAppHostConfigForPath, writeWorkspaceCliPath } from './helpers/fixtures';
import { runProcess, terminateProcessTree } from './helpers/process';
import { getProcessEntry, listProcessEntries, type ProcessEntry } from './helpers/processArguments';
import { ensureDiagnosticsDir, getCliPath, getPrimaryAppHostProjectPath, getRepoRoot, getRunRoot, getWorkspaceRoot } from './helpers/paths';
import { captureScreenshot, closeAllEditors, getOpenEditorTitles, hideAspireView, interactWithModalDialog, openAspireView, setPanelVisible, waitForOpenEditorCount, waitForPanelVisibility, type ModalDialogInteraction } from './helpers/vscode';
import { assertLinkedAppHostCliLaunch, commandLineArgumentEquals } from '../test/helpers/processArguments';
import { getCmdShimSpawnCommand, shouldWrapWithCmd } from '../utils/cmdShimCommand';

interface LifecycleToolResult {
    tool: string;
    outcome: string;
    appHostPath: string;
    requestedMode?: string;
    effectiveMode?: string;
    isolated?: boolean;
    controller: string;
}

interface PreparedInvocation {
    registered: boolean;
    supportsPreparation: boolean;
    invocationMessage?: string;
    confirmationTitle?: string;
    confirmationMessage?: string;
}

interface RegisteredTool {
    name: string;
    tags: string[];
    description: string;
    registered: boolean;
    supportsPreparation: boolean;
}

interface LanguageModelToolInvocationResponse {
    results: string[];
    cancellations: number;
    unexpectedFailures: number;
}

interface ExternalAppHostRun {
    child: ChildProcessWithoutNullStreams;
    completion: Promise<{ exitCode: number | null; signal: NodeJS.Signals | null }>;
    getCompletion(): { result?: { exitCode: number | null; signal: NodeJS.Signals | null }; error?: Error };
    getOutput(): { stdout: string; stderr: string };
}

interface LinkedWorktreeAppHostFixture {
    seedRepositoryPath: string;
    linkedWorktreePath: string;
    appHostPath: string;
    gitFilePath: string;
    gitFileContents: string;
    adminDirectoryPath: string;
    adminBackpointerPath: string;
    adminBackpointerContents: string;
}

interface ExtensionSpawnLog {
    path: string;
    line: string;
}

const startToolName = 'aspire_apphost_start';
const stopToolName = 'aspire_apphost_stop';
const statusToolName = 'aspire_debug_session_status';
const explainToolName = 'aspire_explain_launch_failure';
const listToolName = 'aspire_list_debug_sessions';
const dashboardToolName = 'aspire_open_dashboard';
const outputToolName = 'aspire_open_output';
const hotReloadToolName = 'aspire_hot_reload_status';
const expectedToolNames = [
    startToolName,
    stopToolName,
    statusToolName,
    explainToolName,
    hotReloadToolName,
    listToolName,
    dashboardToolName,
    outputToolName,
];
const appHostArgvEvidenceEnvironmentVariable = 'ASPIRE_EXTENSION_E2E_APPHOST_ARGV_EVIDENCE';
const appHostArgvEvidenceArgumentPrefix = '--e2e-argv-evidence=';

suite('Aspire AppHost lifecycle E2E', function () {
    this.timeout(900000);

    teardown(async () => {
        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'setDashboardBrowserForE2E', value: null }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'AppHost lifecycle language model tool E2E teardown failed.');
    });

    test('starts, refuses to duplicate, and stops the AppHost through vscode.lm.invokeTool', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = path.relative(getWorkspaceRoot(), appHostPath).split(path.sep).join('/');

        const registeredTools = await invokeControlCommand<RegisteredTool[]>({ name: 'getRegisteredLanguageModelTools' });
        assert.deepStrictEqual(registeredTools.map(tool => tool.name), expectedToolNames);
        assert.ok(registeredTools.every(tool => tool.registered));
        assert.deepStrictEqual(
            registeredTools.filter(tool => tool.supportsPreparation).map(tool => tool.name),
            [startToolName, stopToolName, dashboardToolName, outputToolName]);

        // The prepared invocation is also captured directly from the registered tool
        // instance so the exact confirmation strings are asserted, not just what the
        // modal renders.
        const preparedStart = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: startToolName,
            input: { appHostPath: relativeAppHostPath, mode: 'debug' },
        });
        const preparedStop = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: stopToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        const preparedStatus = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        const preparedExplain = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: explainToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        const preparedList = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: listToolName,
            input: {},
        });
        const preparedDashboard = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: dashboardToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        const preparedOutput = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: outputToolName,
            input: {},
        });
        const preparedHotReload = await invokeControlCommand<PreparedInvocation>({
            name: 'prepareLanguageModelToolInvocation',
            toolName: hotReloadToolName,
            input: {},
        });

        assert.strictEqual(preparedStart.confirmationTitle, 'Start Aspire AppHost');
        assert.strictEqual(preparedStart.confirmationMessage, `Start the Aspire AppHost ${relativeAppHostPath} in debug mode?`);
        assert.strictEqual(preparedStop.confirmationTitle, 'Stop Aspire AppHost');
        assert.strictEqual(preparedStop.confirmationMessage, `Stop the Aspire AppHost ${relativeAppHostPath}?`);
        assert.deepStrictEqual(preparedStatus, { registered: true, supportsPreparation: false });
        assert.deepStrictEqual(preparedExplain, { registered: true, supportsPreparation: false });
        assert.deepStrictEqual(preparedList, { registered: true, supportsPreparation: false });
        // Hot Reload only reports, so it neither confirms nor announces an editor change.
        assert.deepStrictEqual(preparedHotReload, { registered: true, supportsPreparation: false });
        assert.deepStrictEqual(preparedDashboard, {
            registered: true,
            supportsPreparation: true,
            invocationMessage: `Opening Aspire Dashboard for ${relativeAppHostPath}...`,
        });
        assert.deepStrictEqual(preparedOutput, {
            registered: true,
            supportsPreparation: true,
            invocationMessage: 'Opening the VS Code Output panel and selecting the Aspire Extension output channel...',
            confirmationTitle: 'Open the VS Code Output panel and select the Aspire Extension output channel',
            confirmationMessage: 'This opens the VS Code Output panel and selects the Aspire Extension output channel.',
        });

        const beforeLaunchStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(beforeLaunchStatus, {
            success: true,
            tool: statusToolName,
            outcome: 'notDebugging',
            scope: 'appHost',
            controller: 'editor',
            appHost: relativeAppHostPath,
        });
        assertSafeEditorAssistanceResult(beforeLaunchStatus);

        const beforeLaunchSessions = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: listToolName,
            input: {},
        });
        assert.deepStrictEqual(beforeLaunchSessions, {
            success: true,
            tool: listToolName,
            outcome: 'noSessions',
            sessions: [],
        });
        assertSafeEditorAssistanceResult(beforeLaunchSessions);

        const beforeLaunchExplanation = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: explainToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(beforeLaunchExplanation, {
            success: true,
            tool: explainToolName,
            outcome: 'noRecordedFailure',
            appHost: relativeAppHostPath,
        });
        assertSafeEditorAssistanceResult(beforeLaunchExplanation);

        // Nothing is running yet, so there is no editor-debugged resource to report on and no
        // active AppHost that could contain a named one. Both fail closed rather than guessing.
        const beforeLaunchHotReload = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: {},
        });
        assert.deepStrictEqual(beforeLaunchHotReload, {
            success: false,
            tool: hotReloadToolName,
            outcome: 'noEditorControlledResource',
        });
        assertSafeEditorAssistanceResult(beforeLaunchHotReload);

        const beforeLaunchNamedHotReload = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'e2e-worker' },
        });
        assert.deepStrictEqual(beforeLaunchNamedHotReload, {
            success: false,
            tool: hotReloadToolName,
            outcome: 'resourceNotFound',
        });
        assertSafeEditorAssistanceResult(beforeLaunchNamedHotReload);

        const editorsBeforeNotRunningDashboard = await getOpenEditorTitles();
        const beforeLaunchDashboard = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: dashboardToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(beforeLaunchDashboard, {
            success: false,
            tool: dashboardToolName,
            outcome: 'appHostNotRunning',
        });
        // The Dashboard tool no longer confirms, so the refusal path is the only thing standing
        // between a model request and editor UI changing. Assert it opened nothing.
        assert.deepStrictEqual(await getOpenEditorTitles(), editorsBeforeNotRunningDashboard);
        assertSafeEditorAssistanceResult(beforeLaunchDashboard);

        const missingAppHost = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: 'Missing/AppHost.csproj' },
        });
        assert.deepStrictEqual(missingAppHost, {
            success: false,
            tool: statusToolName,
            outcome: 'appHostNotFound',
        });
        assertSafeEditorAssistanceResult(missingAppHost);

        const additionalPropertyValidation = await invokeLanguageModelTool({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath, unexpected: true },
            invokeRegisteredToolDirectly: true,
        });
        assertInvocationCompleted(additionalPropertyValidation);
        const [additionalPropertyValidationEvidence] = additionalPropertyValidation.results
            .map(result => JSON.parse(result) as Record<string, unknown>);
        assert.deepStrictEqual([additionalPropertyValidationEvidence], [{
            success: false,
            tool: statusToolName,
            outcome: 'invalidInput',
        }]);
        assertSafeEditorAssistanceResult(additionalPropertyValidationEvidence);

        const debugLaunchesBeforeStart = getDebugLaunchCount();
        // Both calls are fired concurrently inside the extension host: the tool must
        // serialize them per AppHost path so only one of them launches a process.
        const concurrentStartInvocation = await invokeLifecycleTool({
            name: 'invokeLanguageModelTool',
            toolName: startToolName,
            input: { appHostPath: relativeAppHostPath, mode: 'debug' },
            times: 2,
        }, 600000, 2, 'apphost-lifecycle-start-confirmation');
        const concurrentStarts = concurrentStartInvocation.results;

        assert.strictEqual(concurrentStartInvocation.dialogs.length, 2, 'Expected each concurrent start call to require its own confirmation.');
        for (const dialog of concurrentStartInvocation.dialogs) {
            assert.strictEqual(dialog.message, 'Start Aspire AppHost');
            assert.strictEqual(dialog.details, `Start the Aspire AppHost ${relativeAppHostPath} in debug mode?`);
        }

        const startedResults = concurrentStarts.filter(result => result.outcome === 'started');
        const dedupedResults = concurrentStarts.filter(result => result.outcome === 'alreadyStarting' || result.outcome === 'alreadyRunning');
        assert.strictEqual(startedResults.length, 1, `Expected exactly one launch from concurrent start calls. Results: ${JSON.stringify(concurrentStarts)}`);
        assert.strictEqual(dedupedResults.length, 1, `Expected the second concurrent start to be deduplicated. Results: ${JSON.stringify(concurrentStarts)}`);
        assert.strictEqual(startedResults[0].appHostPath, relativeAppHostPath);
        assert.strictEqual(startedResults[0].requestedMode, 'debug');
        assert.strictEqual(startedResults[0].controller, 'editor');

        await waitForDebugSessionStartup(appHostPath, 600000);
        const appHostPids = await waitForAppHostProcessCount(appHostPath, 1, 180000);
        const appHostPid = appHostPids[0];

        const startedSessions = readStateFile().state.debugSessions.filter(session => session.appHostPath !== undefined && isSamePath(session.appHostPath, appHostPath));
        assert.strictEqual(startedSessions.length, 1, 'Expected exactly one editor-owned debug session after the concurrent start calls.');

        const status = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(status, {
            success: true,
            tool: statusToolName,
            outcome: 'running',
            scope: 'appHost',
            controller: 'editor',
            appHost: relativeAppHostPath,
            mode: 'debug',
        });
        assertSafeEditorAssistanceResult(status);

        // The packaged fixture does not install the C# debugger, so the worker runs under
        // the editor-owned AppHost without creating an editor-owned child debug session.
        // This still exercises real CLI resource discovery, including the generated
        // instance name whose logical display name remains "e2e-worker".
        //
        // The bounded resource projection reports whatever the CLI has published, so the call is
        // repeated until the worker is running and healthy rather than asserting against whichever
        // intermediate snapshot the first call happened to observe.
        const resourceStatus = await waitForToolResult<Record<string, unknown>>(
            {
                name: 'invokeLanguageModelTool',
                toolName: statusToolName,
                input: { appHostPath: relativeAppHostPath, resourceName: 'e2e-worker' },
            },
            result => (result.resource as Record<string, unknown> | undefined)?.healthStatus === 'Healthy',
            'a running, healthy e2e-worker resource projection');
        assert.deepStrictEqual(resourceStatus, {
            success: true,
            tool: statusToolName,
            outcome: 'notDebugging',
            scope: 'resource',
            controller: 'editor',
            appHost: relativeAppHostPath,
            resourceName: 'e2e-worker',
            // `exitCode` is absent because the CLI only reports one for a resource that has
            // exited, and `source` is the project file name rather than its path.
            resource: {
                resourceType: 'Project',
                state: 'Running',
                healthStatus: 'Healthy',
                source: 'AspireE2E.Worker.csproj',
            },
        });
        assertSafeEditorAssistanceResult(resourceStatus);

        const missingResourceStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath, resourceName: 'missing-resource' },
        });
        assert.deepStrictEqual(missingResourceStatus, {
            success: false,
            tool: statusToolName,
            outcome: 'resourceNotFound',
            scope: 'resource',
            controller: 'editor',
            appHost: relativeAppHostPath,
            resourceName: 'missing-resource',
        });
        assertSafeEditorAssistanceResult(missingResourceStatus);

        // The E2E extension host installs neither C# Dev Kit nor the C# extension, so Hot Reload
        // is unavailable in this window and `e2e-worker` runs without an editor debug session.
        // Both are reported as bounded evidence while the editor still owns the AppHost.
        //
        // The Aspire view is hidden first, which stops the repository's `describe --follow`
        // streams and leaves it in the cold state a window that never opened the view is in.
        // Answering from that state is the point: the read-only reporters resolve resources with
        // an authoritative read, so no UI has to be shown to make a resource observable.
        await hideAspireView();
        const hotReloadStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'e2e-worker' },
        });
        assert.deepStrictEqual(
            { ...hotReloadStatus, resourceName: undefined },
            {
                success: true,
                tool: hotReloadToolName,
                outcome: 'notApplicable',
                appHost: relativeAppHostPath,
                resourceName: undefined,
                controller: 'editor',
                hotReloadEnabled: false,
                evidence: [
                    'devKitNotInstalled',
                    'hotReloadSettingUnavailable',
                    'hotReloadOnSaveEnabled',
                    'notEditorDebuggedResource',
                    'dotnetProjectResource',
                ],
                fallback: ['restartResource', 'rebuildAndRestartAppHost'],
            });
        // The registry name carries a generated instance suffix, so only its stable logical
        // prefix can be asserted. It is the registry's own name, never the requested selector.
        assert.match(hotReloadStatus.resourceName as string, /^e2e-worker/);
        assertSafeEditorAssistanceResult(hotReloadStatus);

        // Narrowing to the AppHost that owns the resource must produce the same answer, which is
        // what makes the selector usable to disambiguate a name several AppHosts share.
        const narrowedHotReloadStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'e2e-worker', appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(narrowedHotReloadStatus, hotReloadStatus);
        assertSafeEditorAssistanceResult(narrowedHotReloadStatus);

        const missingAppHostHotReload = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'e2e-worker', appHostPath: 'Missing/AppHost.csproj' },
        });
        assert.deepStrictEqual(missingAppHostHotReload, {
            success: false,
            tool: hotReloadToolName,
            outcome: 'appHostNotFound',
        });
        assertSafeEditorAssistanceResult(missingAppHostHotReload);

        const missingResourceHotReload = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'missing-resource' },
        });
        assert.deepStrictEqual(missingResourceHotReload, {
            success: false,
            tool: hotReloadToolName,
            outcome: 'resourceNotFound',
        });
        assertSafeEditorAssistanceResult(missingResourceHotReload);

        const hotReloadInputValidation = await invokeLanguageModelTool({
            name: 'invokeLanguageModelTool',
            toolName: hotReloadToolName,
            input: { resourceName: 'e2e-worker', unexpected: true },
            invokeRegisteredToolDirectly: true,
        });
        assertInvocationCompleted(hotReloadInputValidation);
        const [hotReloadInputValidationEvidence] = hotReloadInputValidation.results
            .map(result => JSON.parse(result) as Record<string, unknown>);
        assert.deepStrictEqual(hotReloadInputValidationEvidence, {
            success: false,
            tool: hotReloadToolName,
            outcome: 'invalidInput',
        });
        assertSafeEditorAssistanceResult(hotReloadInputValidationEvidence);

        const explanation = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: explainToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(explanation, {
            success: true,
            tool: explainToolName,
            outcome: 'noRecordedFailure',
            appHost: relativeAppHostPath,
        });
        assertSafeEditorAssistanceResult(explanation);

        const sessions = await invokeToolWithoutConfirmation<{
            success: boolean;
            tool: string;
            outcome: string;
            sessions: Array<Record<string, unknown>>;
            truncated?: true;
        }>({
            name: 'invokeLanguageModelTool',
            toolName: listToolName,
            input: {},
        });
        assert.strictEqual(sessions.success, true);
        assert.strictEqual(sessions.tool, listToolName);
        assert.strictEqual(sessions.outcome, 'sessionsFound');
        assert.ok(sessions.sessions.length > 0 && sessions.sessions.length <= 20);
        for (const session of sessions.sessions) {
            assert.deepStrictEqual(Object.keys(session).sort(), ['appHost', 'controller', 'mode', 'state']);
        }
        assertSafeEditorAssistanceResult(sessions);

        const canceledStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath },
            cancelBeforeInvocation: true,
        });
        assert.deepStrictEqual(canceledStatus, {
            success: false,
            tool: statusToolName,
            outcome: 'canceled',
        });
        assertSafeEditorAssistanceResult(canceledStatus);

        await executeE2eControlCommand({ name: 'setDashboardBrowserForE2E', value: 'integratedBrowser' });
        await closeAllEditors();
        const dashboardEditorsBeforeOpen = await getOpenEditorTitles();
        const dashboardInvocation = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: dashboardToolName,
            input: { appHostPath: relativeAppHostPath },
        }, 180000);
        assert.strictEqual(dashboardInvocation.success, true);
        assert.strictEqual(dashboardInvocation.tool, dashboardToolName);
        assert.strictEqual(dashboardInvocation.outcome, 'opened');
        assert.strictEqual(dashboardInvocation.presentation, 'integratedBrowser');
        assert.ok((await waitForOpenEditorCount(dashboardEditorsBeforeOpen.length + 1)).length >
            dashboardEditorsBeforeOpen.length);
        await captureScreenshot('editor-assistance-dashboard-opened');
        assert.deepStrictEqual(Object.keys(dashboardInvocation).sort(), ['outcome', 'presentation', 'success', 'tool']);
        assertSafeEditorAssistanceResult(dashboardInvocation);

        await setPanelVisible(false);
        const deniedOutputInvocation = await invokeLanguageModelToolWithConfirmations<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: outputToolName,
            input: {},
        }, 120000, 1, 'editor-assistance-output-confirmation-denied', 'Cancel');
        assertDeniedToolInvocation(deniedOutputInvocation, outputToolName);
        await waitForPanelVisibility(false);
        assertSafeEditorAssistanceResult(deniedOutputInvocation);

        const outputInvocation = await invokeLanguageModelToolWithConfirmations<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: outputToolName,
            input: {},
        }, 120000, 1, 'editor-assistance-output-confirmation');
        assert.strictEqual(outputInvocation.dialogs[0].message, 'Open the VS Code Output panel and select the Aspire Extension output channel');
        assert.strictEqual(outputInvocation.dialogs[0].details, 'This opens the VS Code Output panel and selects the Aspire Extension output channel.');
        assert.deepStrictEqual(outputInvocation.results, [{
            success: true,
            tool: outputToolName,
            outcome: 'opened',
        }]);
        assertInvocationCompleted(outputInvocation);
        await waitForPanelVisibility(true);
        assertSafeEditorAssistanceResult(outputInvocation.results[0]);

        const repeatedStartInvocation = await invokeLifecycleTool({
            name: 'invokeLanguageModelTool',
            toolName: startToolName,
            input: { appHostPath: relativeAppHostPath, mode: 'run' },
        }, 180000, 1);
        const repeatedStart = repeatedStartInvocation.results;
        assert.strictEqual(repeatedStartInvocation.dialogs[0].details, `Start the Aspire AppHost ${relativeAppHostPath} in run mode?`);
        assert.strictEqual(repeatedStart.length, 1);
        assert.strictEqual(repeatedStart[0].outcome, 'alreadyRunning');
        assert.strictEqual(repeatedStart[0].controller, 'editor');
        assert.strictEqual(repeatedStart[0].requestedMode, 'run');
        // The running session keeps its own mode: a start call cannot silently switch a
        // debug session to a run session.
        assert.strictEqual(repeatedStart[0].effectiveMode, 'debug');

        const sessionsAfterRepeatedStart = readStateFile().state.debugSessions.filter(session => session.appHostPath !== undefined && isSamePath(session.appHostPath, appHostPath));
        assert.strictEqual(sessionsAfterRepeatedStart.length, 1, 'Expected the repeated start call to leave a single debug session.');
        assert.deepStrictEqual(await findAppHostProcessIds(appHostPath), [appHostPid], 'Expected the repeated start call to leave the original AppHost process running.');
        assert.strictEqual(getDebugLaunchCount() - debugLaunchesBeforeStart, 1, 'Expected exactly one AppHost launch across all start calls.');

        const stopInvocation = await invokeLifecycleTool({
            name: 'invokeLanguageModelTool',
            toolName: stopToolName,
            input: { appHostPath: relativeAppHostPath },
        }, 300000, 1, 'apphost-lifecycle-stop-confirmation');
        const stopResults = stopInvocation.results;
        assert.strictEqual(stopInvocation.dialogs[0].message, 'Stop Aspire AppHost');
        assert.strictEqual(stopInvocation.dialogs[0].details, `Stop the Aspire AppHost ${relativeAppHostPath}?`);
        assert.strictEqual(stopResults.length, 1);
        assert.strictEqual(stopResults[0].outcome, 'stopped');
        assert.strictEqual(stopResults[0].controller, 'editor');
        assert.strictEqual(stopResults[0].appHostPath, relativeAppHostPath);

        await waitForNoDebugSessions(180000);
        await waitForProcessExit(appHostPid, 180000);
        assert.strictEqual(readStateFile().state.debugSessions.length, 0, 'Expected no debug sessions after the stop tool call.');
        assert.deepStrictEqual(await waitForAppHostProcessCount(appHostPath, 0, 180000), [], 'Expected no AppHost processes after the stop tool call.');

        const afterStopStatus = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: statusToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(afterStopStatus, {
            success: true,
            tool: statusToolName,
            outcome: 'notDebugging',
            scope: 'appHost',
            controller: 'editor',
            appHost: relativeAppHostPath,
        });
        assertSafeEditorAssistanceResult(afterStopStatus);

        const afterStopSessions = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: listToolName,
            input: {},
        });
        assert.deepStrictEqual(afterStopSessions, {
            success: true,
            tool: listToolName,
            outcome: 'noSessions',
            sessions: [],
        });
        assertSafeEditorAssistanceResult(afterStopSessions);

        const afterStopDashboard = await invokeToolWithoutConfirmation<Record<string, unknown>>({
            name: 'invokeLanguageModelTool',
            toolName: dashboardToolName,
            input: { appHostPath: relativeAppHostPath },
        });
        assert.deepStrictEqual(afterStopDashboard, {
            success: false,
            tool: dashboardToolName,
            outcome: 'appHostNotRunning',
        });
        assertSafeEditorAssistanceResult(afterStopDashboard);

        const stopAgainResults = (await invokeLifecycleTool({
            name: 'invokeLanguageModelTool',
            toolName: stopToolName,
            input: { appHostPath: relativeAppHostPath },
        }, 120000, 1)).results;
        assert.strictEqual(stopAgainResults[0].outcome, 'notRunning');
        assert.strictEqual(stopAgainResults[0].controller, 'none');

        writeLifecycleToolArtifact({
            relativeAppHostPath,
            registeredToolNames: registeredTools.map(tool => tool.name),
            preparedStart,
            preparedStop,
            preparedStatus,
            preparedExplain,
            preparedList,
            preparedDashboard,
            preparedOutput,
            preparedHotReload,
            beforeLaunchStatus,
            beforeLaunchSessions,
            beforeLaunchExplanation,
            beforeLaunchDashboard,
            beforeLaunchHotReload,
            beforeLaunchNamedHotReload,
            missingAppHost,
            additionalPropertyValidation: additionalPropertyValidationEvidence,
            status,
            resourceStatus,
            missingResourceStatus,
            hotReloadStatus,
            narrowedHotReloadStatus,
            missingAppHostHotReload,
            missingResourceHotReload,
            hotReloadInputValidation: hotReloadInputValidationEvidence,
            explanation,
            sessions,
            canceledStatus,
            dashboard: dashboardInvocation,
            deniedOutput: deniedOutputInvocation,
            output: outputInvocation.results[0],
            confirmationDialogs: [
                ...concurrentStartInvocation.dialogs,
                ...deniedOutputInvocation.dialogs,
                ...outputInvocation.dialogs,
                repeatedStartInvocation.dialogs[0],
                stopInvocation.dialogs[0],
            ],
            concurrentStarts,
            repeatedStart: repeatedStart[0],
            stop: stopResults[0],
            afterStopStatus,
            afterStopSessions,
            afterStopDashboard,
            stopAgain: stopAgainResults[0],
        });
    });

    test('stops a CLI-started AppHost through vscode.lm.invokeTool', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = path.relative(getWorkspaceRoot(), appHostPath).split(path.sep).join('/');
        const externalRun = startExternalAppHost(appHostPath);
        let externalAppHostPid: number | undefined;

        try {
            externalAppHostPid = await waitForExternalAppHost(externalRun, appHostPath, 600000);
            assert.strictEqual(readStateFile().state.debugSessions.length, 0, 'Expected a CLI-started AppHost to have no editor debug session.');

            const stopInvocation = await invokeLifecycleTool({
                name: 'invokeLanguageModelTool',
                toolName: stopToolName,
                input: { appHostPath: relativeAppHostPath },
            }, 300000, 1, 'apphost-lifecycle-external-stop-confirmation');

            assert.strictEqual(stopInvocation.dialogs[0].message, 'Stop Aspire AppHost');
            assert.strictEqual(stopInvocation.dialogs[0].details, `Stop the Aspire AppHost ${relativeAppHostPath}?`);
            const [stopResult] = stopInvocation.results;
            assert.deepStrictEqual(
                { outcome: stopResult.outcome, controller: stopResult.controller },
                stopResult.outcome === 'stopped'
                    ? { outcome: 'stopped', controller: 'external' }
                    // The external process can exit after discovery but before the
                    // post-confirmation recheck. Failing closed as notRunning is the
                    // correct race-safe result; the process-exit assertions below still
                    // prove that no external AppHost survives the invocation.
                    : { outcome: 'notRunning', controller: 'none' });
            assert.strictEqual(stopResult.tool, stopToolName);
            assert.strictEqual(stopResult.appHostPath, relativeAppHostPath);

            await waitForNoRunningAppHost(180000, appHostPath);
            await waitForProcessExit(externalAppHostPid, 180000);
            if (stopResult.outcome === 'notRunning' &&
                externalRun.child.exitCode === null &&
                externalRun.child.signalCode === null) {
                terminateProcessTree(externalRun.child.pid, 'SIGTERM');
            }
            await waitForChildProcessExit(externalRun, 180000);
            assert.strictEqual(readStateFile().state.debugSessions.length, 0, 'Expected external stop to leave editor debug sessions untouched.');
        }
        finally {
            if (externalRun.child.exitCode === null && externalRun.child.signalCode === null) {
                terminateProcessTree(externalRun.child.pid, 'SIGKILL');
                await waitForChildProcessExit(externalRun, 30000).catch(() => undefined);
            }
            if (externalAppHostPid !== undefined && isProcessRunning(externalAppHostPid)) {
                await stopAppHostIfRunning(appHostPath).catch(() => undefined);
            }
        }
    });

    suite('linked-worktree isolation', function () {
        const launchConfigurationName = 'Aspire linked worktree exact argv proof';
        const launchJsonPath = path.join(getWorkspaceRoot(), '.vscode', 'launch.json');
        let originalLaunchJson: string | undefined;
        let fixture: LinkedWorktreeAppHostFixture | undefined;

        suiteSetup(async () => {
            await openAspireView();
            await waitForWorkspaceAppHost();
            await waitForRepositoryIdle();
            originalLaunchJson = fs.existsSync(launchJsonPath) ? fs.readFileSync(launchJsonPath, 'utf8') : undefined;
            fixture = await createLinkedWorktreeAppHostFixture();
        });

        teardown(async () => {
            if (fixture) {
                await resetLinkedWorktreeAppHostFixture(fixture, launchJsonPath, originalLaunchJson);
            }
        });

        suiteTeardown(async () => {
            if (fixture) {
                await cleanupLinkedWorktreeAppHostFixture(fixture, launchJsonPath, originalLaunchJson);
            }
        });

        test('runAspireCli requires a workspace-relative working directory', async () => {
            const linkedFixture = fixture;
            assert.ok(linkedFixture);
            const relativeWorkingDirectory = path.relative(getWorkspaceRoot(), linkedFixture.linkedWorktreePath);

            await assert.rejects(
                () => invokeControlCommand({
                    name: 'runAspireCli',
                    args: ['--version'],
                    workingDirectory: linkedFixture.linkedWorktreePath,
                }),
                /runAspireCli workingDirectory must be workspace-relative/);
            await assert.rejects(
                () => invokeControlCommand({
                    name: 'runAspireCli',
                    args: ['--version'],
                    workingDirectory: path.join('..', path.basename(linkedFixture.linkedWorktreePath)),
                }),
                /runAspireCli workingDirectory must stay inside the configured E2E workspace root/);

            const result = await invokeControlCommand<{ exitCode: number | null }>({
                name: 'runAspireCli',
                args: ['--version'],
                workingDirectory: relativeWorkingDirectory,
            });
            assert.strictEqual(result.exitCode, 0);
        });

        test('runAspireCli waits for timed-out process tree cleanup', async () => {
            const linkedFixture = fixture;
            assert.ok(linkedFixture);
            const wrapper = writeTimeoutCliWrapper();
            let descendantPid: number | undefined;

            try {
                await writeWorkspaceCliPath(wrapper.cliPath);
                await setE2eCliPathForE2E(wrapper.cliPath);
                const invocation = invokeControlCommand({
                    name: 'runAspireCli',
                    args: ['timeout-tree'],
                    workingDirectory: path.relative(getWorkspaceRoot(), linkedFixture.linkedWorktreePath),
                    timeoutMs: 1000,
                }, 30000);
                invocation.catch(() => undefined);

                descendantPid = await waitForProcessIdFile(wrapper.pidPath, 10000);
                await assert.rejects(invocation, /timed out after 1000ms/);
                assert.strictEqual(
                    await getProcessEntry(descendantPid),
                    undefined,
                    `Expected descendant process ${descendantPid} to be gone before runAspireCli rejected.`);
            }
            finally {
                await runE2eTeardown([
                    () => restoreE2eCliPathForE2E(),
                    () => restoreWorkspaceCliPath(),
                ], 'runAspireCli timeout process tree E2E cleanup failed.');
                if (descendantPid !== undefined && isProcessRunning(descendantPid)) {
                    terminateProcessTree(descendantPid, 'SIGKILL');
                }
                fs.rmSync(wrapper.directory, { recursive: true, force: true });
            }
        });

        test('runAspireCli redacts forwarded values and output from errors', async () => {
            const linkedFixture = fixture;
            assert.ok(linkedFixture);
            const wrapper = writeTimeoutCliWrapper();
            const forwardedValue = `forwarded-${process.pid}-${Date.now()}`;
            let descendantPid: number | undefined;

            try {
                await writeWorkspaceCliPath(wrapper.cliPath);
                await setE2eCliPathForE2E(wrapper.cliPath);

                const timeoutError = await captureError(() => invokeControlCommand({
                    name: 'runAspireCli',
                    args: ['timeout-tree', '--', forwardedValue],
                    workingDirectory: path.relative(getWorkspaceRoot(), linkedFixture.linkedWorktreePath),
                    timeoutMs: 1000,
                }, 30000));
                descendantPid = await waitForProcessIdFile(wrapper.pidPath, 10000);
                if (timeoutError.message.includes(forwardedValue)) {
                    throw new Error('runAspireCli timeout diagnostics exposed a forwarded argument or command output.');
                }
                assert.match(timeoutError.message, /timeout-tree -- <redacted> timed out after 1000ms/);
                assert.strictEqual(timeoutError.message.includes('stdout:'), false);
                assert.strictEqual(timeoutError.message.includes('stderr:'), false);

                const nonzeroError = await captureError(() => invokeControlCommand({
                    name: 'runAspireCli',
                    args: ['fail', '--', forwardedValue],
                    workingDirectory: path.relative(getWorkspaceRoot(), linkedFixture.linkedWorktreePath),
                }, 30000));
                if (nonzeroError.message.includes(forwardedValue)) {
                    throw new Error('runAspireCli nonzero-exit diagnostics exposed a forwarded argument or command output.');
                }
                assert.match(nonzeroError.message, /fail -- <redacted> exited with code 23/);
                assert.strictEqual(nonzeroError.message.includes('stdout:'), false);
                assert.strictEqual(nonzeroError.message.includes('stderr:'), false);
            }
            finally {
                await runE2eTeardown([
                    () => restoreE2eCliPathForE2E(),
                    () => restoreWorkspaceCliPath(),
                ], 'runAspireCli redaction E2E cleanup failed.');
                if (descendantPid !== undefined && isProcessRunning(descendantPid)) {
                    terminateProcessTree(descendantPid, 'SIGKILL');
                }
                fs.rmSync(wrapper.directory, { recursive: true, force: true });
            }
        });

        test('starts a linked-worktree AppHost with inferred isolation through vscode.lm.invokeTool', async () => {
            assert.ok(fixture);
            let artifact: Record<string, unknown> = {};

            try {
                const relativeAppHostPath = path.relative(getWorkspaceRoot(), fixture.appHostPath).split(path.sep).join('/');
                artifact = {
                    status: 'created',
                    ...fixture,
                    relativeAppHostPath,
                    cli: {
                        path: getCliPath(),
                        version: (await runProcess(getCliPath(), ['--version'], { timeoutMs: 60000 })).stdout.trim(),
                        repositoryHead: (await runProcess('git', ['rev-parse', 'HEAD'], { cwd: getRepoRoot(), timeoutMs: 30000 })).stdout.trim(),
                    },
                };
                writeLinkedWorktreeArtifact('lm', artifact);

                writeWorkspaceAppHostConfigForPath(fixture.appHostPath);
                const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
                await executeE2eControlCommand({ name: 'refreshAppHosts' });
                await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
                const discovered = await waitForSelectedWorkspaceAppHost(fixture.appHostPath);
                assert.ok(discovered.state.workspaceAppHostPath && isSamePath(discovered.state.workspaceAppHostPath, fixture.appHostPath));

                const preparedStart = await invokeControlCommand<PreparedInvocation>({
                    name: 'prepareLanguageModelToolInvocation',
                    toolName: startToolName,
                    input: { appHostPath: relativeAppHostPath, mode: 'debug' },
                });
                // The AppHost lives in a linked worktree and `isolated` was omitted, so the
                // lifecycle tool infers isolation. The confirmation has to disclose that, because
                // this dialog is what "Always allow" is granted against.
                const expectedConfirmation = `Start the Aspire AppHost ${relativeAppHostPath} in debug mode with isolation?`;
                assert.strictEqual(preparedStart.confirmationTitle, 'Start Aspire AppHost');
                assert.strictEqual(preparedStart.confirmationMessage, expectedConfirmation);

                const startInvocation = await invokeLifecycleTool({
                    name: 'invokeLanguageModelTool',
                    toolName: startToolName,
                    input: { appHostPath: relativeAppHostPath, mode: 'debug' },
                }, 600000, 1, 'apphost-lifecycle-linked-worktree-start-confirmation');
                assert.strictEqual(startInvocation.dialogs[0].message, 'Start Aspire AppHost');
                assert.strictEqual(startInvocation.dialogs[0].details, expectedConfirmation);
                assert.deepStrictEqual(startInvocation.results, [{
                    tool: startToolName,
                    outcome: 'started',
                    appHostPath: relativeAppHostPath,
                    requestedMode: 'debug',
                    effectiveMode: 'debug',
                    isolated: true,
                    controller: 'editor',
                }]);

                await waitForDebugSessionStartup(fixture.appHostPath, 600000);
                const processInfoStatus = await executeE2eControlCommand({ name: 'getDebugSessionProcessInfo', appHostPath: fixture.appHostPath });
                const processInfo = processInfoStatus.result as { appHostPath?: string; cliPid?: number; appHostPid?: number };
                assert.ok(processInfo.appHostPath && isSamePath(processInfo.appHostPath, fixture.appHostPath));
                assert.ok(processInfo.cliPid, `Expected the E2E state bridge to report the linked AppHost CLI process: ${JSON.stringify(processInfoStatus)}`);

                const cliProcess = await waitForLinkedAppHostCliProcess(processInfo.cliPid, fixture.appHostPath, 180000);
                const extensionLog = await waitForLinkedAppHostSpawnLog(fixture.appHostPath, 60000);
                const runningState = readStateFile();
                assert.ok(runningState.state.workspaceAppHostPath && isSamePath(runningState.state.workspaceAppHostPath, fixture.appHostPath));
                const activeDebugSession = runningState.state.debugSessions.find(session =>
                    fixture && session.appHostPath && isSamePath(session.appHostPath, fixture.appHostPath) && session.startupCompleted);
                assert.ok(activeDebugSession, `Expected an active debug session for ${fixture.appHostPath}.`);

                Object.assign(artifact, {
                    status: 'running',
                    preparedStart,
                    startConfirmation: startInvocation.dialogs[0],
                    startResult: startInvocation.results[0],
                    processInfo,
                    cliProcess,
                    extensionLog,
                    workspaceAppHostPath: runningState.state.workspaceAppHostPath,
                    activeDebugSession,
                });
                writeLinkedWorktreeArtifact('lm', artifact);

                const stopInvocation = await invokeLifecycleTool({
                    name: 'invokeLanguageModelTool',
                    toolName: stopToolName,
                    input: { appHostPath: relativeAppHostPath },
                }, 300000, 1, 'apphost-lifecycle-linked-worktree-stop-confirmation');
                assert.strictEqual(stopInvocation.dialogs[0].message, 'Stop Aspire AppHost');
                assert.strictEqual(stopInvocation.dialogs[0].details, `Stop the Aspire AppHost ${relativeAppHostPath}?`);
                assert.strictEqual(stopInvocation.results.length, 1);
                assert.strictEqual(stopInvocation.results[0].tool, stopToolName);
                assert.strictEqual(stopInvocation.results[0].outcome, 'stopped');
                assert.strictEqual(stopInvocation.results[0].appHostPath, relativeAppHostPath);
                assert.strictEqual(stopInvocation.results[0].controller, 'editor');

                await waitForNoDebugSessions(180000);
                await waitForNoRunningAppHost(180000, fixture.appHostPath);
                Object.assign(artifact, {
                    status: 'passed',
                    stopConfirmation: stopInvocation.dialogs[0],
                    stopResult: stopInvocation.results[0],
                });
                writeLinkedWorktreeArtifact('lm', artifact);
            }
            catch (error) {
                Object.assign(artifact, {
                    status: 'failed',
                    error: error instanceof Error ? `${error.name}: ${error.message.split(/\r?\n/, 1)[0]}` : String(error),
                });
                writeLinkedWorktreeArtifact('lm', artifact);
                throw error;
            }
        });

        test('Aspire CLI start-debug-session forwards exact linked-worktree AppHost argv without inferred root isolation', async () => {
            assert.ok(fixture);
            const diagnosticsDirectory = ensureDiagnosticsDir();
            const argvEvidencePath = path.join(diagnosticsDirectory, 'apphost-direct-cli-argv-live.json');
            const appHostArguments = [
                `${appHostArgvEvidenceArgumentPrefix}${argvEvidencePath}`,
                '--custom',
                'value with spaces',
                '',
                'literal "quote"',
                path.join('e2e path with spaces', 'backslash\\segment'),
                '--detach',
            ];
            const cliArguments = [
                'run',
                '--start-debug-session',
                '--apphost',
                fixture.appHostPath,
                '--',
                ...appHostArguments,
            ];
            const workingDirectory = path.relative(getWorkspaceRoot(), fixture.linkedWorktreePath);
            let artifact: Record<string, unknown> = {};

            try {
                fs.rmSync(argvEvidencePath, { force: true });
                artifact = {
                    status: 'created',
                    ...fixture,
                    cliArguments,
                    appHostArguments,
                    workingDirectory,
                    argvEvidencePath,
                    cli: {
                        path: getCliPath(),
                        version: (await runProcess(getCliPath(), ['--version'], { timeoutMs: 60000 })).stdout.trim(),
                        repositoryHead: (await runProcess('git', ['rev-parse', 'HEAD'], { cwd: getRepoRoot(), timeoutMs: 30000 })).stdout.trim(),
                    },
                };
                writeLinkedWorktreeArtifact('direct', artifact);

                const cliResult = await invokeControlCommand<{ exitCode: number | null; stdout: string; stderr: string }>({
                    name: 'runAspireCli',
                    args: cliArguments,
                    workingDirectory,
                    timeoutMs: 600000,
                }, 600000);
                assert.strictEqual(cliResult.exitCode, 0);

                await waitForDebugSessionStartup(fixture.appHostPath, 600000);
                const processInfo = await invokeControlCommand<{ appHostPath?: string; cliPid?: number; appHostPid?: number }>({
                    name: 'getDebugSessionProcessInfo',
                    appHostPath: fixture.appHostPath,
                });
                assert.ok(processInfo.cliPid, `Expected the E2E state bridge to report the direct CLI process: ${JSON.stringify(processInfo)}`);
                const cliProcess = await waitForExactLinkedAppHostCliProcess(
                    processInfo.cliPid,
                    getCliPath(),
                    fixture.appHostPath,
                    appHostArguments,
                    180000,
                    false);
                const appHostArgv = await waitForAppHostArgvEvidence(argvEvidencePath, 180000);
                assert.deepStrictEqual(appHostArgv, appHostArguments);

                const runningState = readStateFile();
                const activeDebugSession = runningState.state.debugSessions.find(session =>
                    fixture && session.appHostPath && isSamePath(session.appHostPath, fixture.appHostPath) && session.startupCompleted);
                assert.ok(activeDebugSession, `Expected an active debug session for ${fixture.appHostPath}.`);

                Object.assign(artifact, {
                    status: 'passed',
                    cliResult,
                    cliProcess,
                    processInfo,
                    appHostArgv,
                    activeDebugSession,
                });
                writeLinkedWorktreeArtifact('direct', artifact);
            }
            catch (error) {
                Object.assign(artifact, {
                    status: 'failed',
                    error: error instanceof Error ? `${error.name}: ${error.message.split(/\r?\n/, 1)[0]}` : String(error),
                });
                writeLinkedWorktreeArtifact('direct', artifact);
                throw error;
            }
            finally {
                await resetLinkedWorktreeAppHostFixture(fixture, launchJsonPath, originalLaunchJson);
                fs.rmSync(argvEvidencePath, { force: true });
            }
        });

        test('launch.json F5 preserves exact linked-worktree .NET AppHost argv twice without inferred root isolation', async () => {
            assert.ok(fixture);
            const diagnosticsDirectory = ensureDiagnosticsDir();
            const argvEvidencePath = path.join(diagnosticsDirectory, 'apphost-f5-argv-live.json');
            const cliInvocationLogPath = path.join(diagnosticsDirectory, 'apphost-f5-cli-invocations.jsonl');
            const appHostArguments = [
                '--custom',
                'value with spaces',
                '',
                'literal "quote"',
                path.join('e2e path with spaces', 'backslash\\segment'),
            ];
            let artifact: Record<string, unknown> = {};

            try {
                const cliWrapperPath = writeTokenlessStableCliWrapper(cliInvocationLogPath);
                const relativeAppHostPath = path.relative(getWorkspaceRoot(), fixture.appHostPath).split(path.sep).join('/');
                artifact = {
                    status: 'created',
                    ...fixture,
                    relativeAppHostPath,
                    launchJsonPath,
                    launchConfigurationName,
                    appHostArguments,
                    argvEvidencePath,
                    cliInvocationLogPath,
                    cli: {
                        wrapperPath: cliWrapperPath,
                        realPath: getCliPath(),
                        wrapperVersion: '13.2.0',
                        realVersion: (await runProcess(getCliPath(), ['--version'], { timeoutMs: 60000 })).stdout.trim(),
                        repositoryHead: (await runProcess('git', ['rev-parse', 'HEAD'], { cwd: getRepoRoot(), timeoutMs: 30000 })).stdout.trim(),
                    },
                };
                writeLinkedWorktreeArtifact('f5', artifact);

                await writeWorkspaceCliPath(cliWrapperPath);
                await setE2eCliPathForE2E(cliWrapperPath);

                writeLaunchJson(launchJsonPath, {
                    version: '0.2.0',
                    configurations: [{
                        type: 'aspire',
                        request: 'launch',
                        name: launchConfigurationName,
                        program: fixture.appHostPath,
                        command: 'run',
                        args: ['--', ...appHostArguments],
                        env: {
                            [appHostArgvEvidenceEnvironmentVariable]: argvEvidencePath,
                        },
                    }],
                });

                const passes: Record<string, unknown>[] = [];
                for (let pass = 1; pass <= 2; pass++) {
                    fs.rmSync(argvEvidencePath, { force: true });
                    const invocationCountBeforeLaunch = getCliWrapperInvocations(cliInvocationLogPath).length;
                    const debugLaunchesBefore = getDebugLaunchCount();
                    const startStatus = await executeE2eControlCommand({
                        name: 'startDebugging',
                        configurationName: launchConfigurationName,
                    }, { timeoutMs: 600000 });
                    assert.strictEqual(startStatus.result, true, `Expected VS Code to start launch.json configuration '${launchConfigurationName}' on pass ${pass}.`);

                    await waitForDebugSessionStartup(fixture.appHostPath, 600000);
                    const debugLaunchCountDelta = getDebugLaunchCount() - debugLaunchesBefore;

                    const processInfoStatus = await executeE2eControlCommand({ name: 'getDebugSessionProcessInfo', appHostPath: fixture.appHostPath });
                    const processInfo = processInfoStatus.result as { appHostPath?: string; cliPid?: number; appHostPid?: number };
                    assert.ok(processInfo.appHostPath && isSamePath(processInfo.appHostPath, fixture.appHostPath));
                    assert.ok(processInfo.cliPid, `Expected the E2E state bridge to report the linked AppHost CLI process on pass ${pass}: ${JSON.stringify(processInfoStatus)}`);

                    const cliProcess = await waitForExactLinkedAppHostCliProcess(
                        processInfo.cliPid,
                        cliWrapperPath,
                        fixture.appHostPath,
                        appHostArguments,
                        180000,
                        false);
                    const cliInvocations = waitForCliFallbackAndLaunchInvocations(
                        cliInvocationLogPath,
                        invocationCountBeforeLaunch,
                        fixture.appHostPath,
                        appHostArguments,
                        false);
                    const extensionLog = await waitForLinkedAppHostSpawnLog(fixture.appHostPath, 60000);
                    const appHostArgv = await waitForAppHostArgvEvidence(argvEvidencePath, 180000);
                    assert.deepStrictEqual(appHostArgv, appHostArguments);

                    const retainedArgvEvidencePath = path.join(diagnosticsDirectory, `apphost-f5-argv-pass-${pass}.json`);
                    fs.copyFileSync(argvEvidencePath, retainedArgvEvidencePath);
                    const runningState = readStateFile();
                    const activeDebugSession = runningState.state.debugSessions.find(session =>
                        fixture && session.appHostPath && isSamePath(session.appHostPath, fixture.appHostPath) && session.startupCompleted);
                    assert.ok(activeDebugSession, `Expected an active debug session for ${fixture.appHostPath} on pass ${pass}.`);

                    passes.push({
                        pass,
                        startResult: startStatus.result,
                        debugLaunchCountDelta,
                        processInfo,
                        cliProcess,
                        cliInvocations,
                        extensionLog,
                        appHostArgv,
                        retainedArgvEvidencePath,
                        activeDebugSession,
                    });
                    Object.assign(artifact, {
                        status: `pass-${pass}-running`,
                        passes,
                        workspaceAppHostPath: runningState.state.workspaceAppHostPath,
                    });
                    writeLinkedWorktreeArtifact('f5', artifact);

                    await executeE2eControlCommand({ name: 'stopDebugging' });
                    await waitForNoDebugSessions(180000);
                    await waitForNoRunningAppHost(180000, fixture.appHostPath);
                }

                Object.assign(artifact, {
                    status: 'passed',
                    passes,
                });
                writeLinkedWorktreeArtifact('f5', artifact);
            }
            catch (error) {
                Object.assign(artifact, {
                    status: 'failed',
                    error: error instanceof Error ? `${error.name}: ${error.message.split(/\r?\n/, 1)[0]}` : String(error),
                });
                writeLinkedWorktreeArtifact('f5', artifact);
                throw error;
            }
        });
    });
});

async function createLinkedWorktreeAppHostFixture(): Promise<LinkedWorktreeAppHostFixture> {
    const runRoot = getRunRoot();
    assert.ok(runRoot, 'ASPIRE_EXTENSION_E2E_RUN_ROOT is required to create a linked-worktree AppHost fixture.');

    const seedRepositoryPath = path.join(runRoot, 'apphost lifecycle linked worktree seed');
    const linkedWorktreePath = path.join(getWorkspaceRoot(), 'AspireE2E Linked Worktree');
    await removeLinkedWorktreePaths(seedRepositoryPath, linkedWorktreePath);
    fs.mkdirSync(seedRepositoryPath, { recursive: true });

    try {
        await runProcess('git', ['init'], { cwd: seedRepositoryPath, timeoutMs: 30000 });
        await runProcess('git', ['config', 'user.email', 'aspire-extension-e2e@example.invalid'], { cwd: seedRepositoryPath, timeoutMs: 30000 });
        await runProcess('git', ['config', 'user.name', 'Aspire Extension E2E'], { cwd: seedRepositoryPath, timeoutMs: 30000 });

        const sdkVersion = process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
        assert.ok(sdkVersion, 'ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION is required to create a linked-worktree AppHost fixture.');
        const projectDirectory = path.join(seedRepositoryPath, 'LinkedAppHost');
        fs.mkdirSync(projectDirectory, { recursive: true });
        fs.writeFileSync(path.join(projectDirectory, 'LinkedAppHost.csproj'), `<Project Sdk="Aspire.AppHost.Sdk/${sdkVersion}">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
`);
        fs.writeFileSync(path.join(projectDirectory, 'AppHost.cs'), `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

const string argvEvidenceArgumentPrefix = "${appHostArgvEvidenceArgumentPrefix}";
var appHostArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
var argvEvidenceArgument = appHostArguments.FirstOrDefault(argument =>
    argument.StartsWith(argvEvidenceArgumentPrefix, StringComparison.Ordinal));
var argvEvidencePath = argvEvidenceArgument is not null
    ? argvEvidenceArgument[argvEvidenceArgumentPrefix.Length..]
    : Environment.GetEnvironmentVariable("${appHostArgvEvidenceEnvironmentVariable}");
if (!string.IsNullOrEmpty(argvEvidencePath))
{
    var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(argvEvidencePath));
    if (evidenceDirectory is not null)
    {
        Directory.CreateDirectory(evidenceDirectory);
    }

    File.WriteAllText(argvEvidencePath, JsonSerializer.Serialize(appHostArguments));
}

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`);

        await runProcess('git', ['add', '.'], { cwd: seedRepositoryPath, timeoutMs: 30000 });
        await runProcess('git', ['commit', '-m', 'Seed linked AppHost'], { cwd: seedRepositoryPath, timeoutMs: 30000 });
        await runProcess('git', ['worktree', 'add', '-b', 'e2e-linked-worktree', linkedWorktreePath], { cwd: seedRepositoryPath, timeoutMs: 60000 });

        const appHostPath = path.join(linkedWorktreePath, 'LinkedAppHost', 'LinkedAppHost.csproj');
        assert.ok(fs.existsSync(appHostPath), `Expected the linked worktree to contain ${appHostPath}.`);

        const gitFilePath = path.join(linkedWorktreePath, '.git');
        assert.strictEqual(fs.statSync(gitFilePath).isFile(), true, 'Expected a genuine linked worktree .git file.');
        const gitFileContents = fs.readFileSync(gitFilePath, 'utf8').trim();
        const gitDirectoryMatch = /^gitdir:\s*(.+)$/i.exec(gitFileContents);
        assert.ok(gitDirectoryMatch, `Expected ${gitFilePath} to contain a gitdir pointer.`);
        const adminDirectoryPath = resolveGitMetadataPath(path.dirname(gitFilePath), gitDirectoryMatch[1]);
        assert.strictEqual(path.basename(path.dirname(adminDirectoryPath)), 'worktrees', 'Expected the linked-worktree admin directory below worktrees/.');
        assert.strictEqual(fs.statSync(adminDirectoryPath).isDirectory(), true, 'Expected the linked-worktree admin directory to exist.');

        const adminBackpointerPath = path.join(adminDirectoryPath, 'gitdir');
        const adminBackpointerContents = fs.readFileSync(adminBackpointerPath, 'utf8').trim();
        const resolvedBackpointer = fs.realpathSync.native(resolveGitMetadataPath(adminDirectoryPath, adminBackpointerContents));
        assert.ok(
            isSamePath(resolvedBackpointer, fs.realpathSync.native(gitFilePath)),
            `Expected ${adminBackpointerPath} to point back to ${gitFilePath}.`);

        return {
            seedRepositoryPath,
            linkedWorktreePath,
            appHostPath,
            gitFilePath,
            gitFileContents,
            adminDirectoryPath,
            adminBackpointerPath,
            adminBackpointerContents,
        };
    }
    catch (error) {
        await removeLinkedWorktreePaths(seedRepositoryPath, linkedWorktreePath);
        throw error;
    }
}

async function cleanupLinkedWorktreeAppHostFixture(
    fixture: LinkedWorktreeAppHostFixture,
    launchJsonPath: string,
    originalLaunchJson: string | undefined
): Promise<void> {
    await runE2eTeardown([
        () => resetLinkedWorktreeAppHostFixture(fixture, launchJsonPath, originalLaunchJson),
        () => removeLinkedWorktreePaths(fixture.seedRepositoryPath, fixture.linkedWorktreePath),
    ], 'Linked-worktree AppHost fixture cleanup failed.');
}

async function resetLinkedWorktreeAppHostFixture(
    fixture: LinkedWorktreeAppHostFixture,
    launchJsonPath: string,
    originalLaunchJson: string | undefined
): Promise<void> {
    await runE2eTeardown([
        () => executeE2eControlCommand({ name: 'stopDebugging' }),
        () => stopAppHostIfRunning(fixture.appHostPath),
        () => waitForNoDebugSessions(180000),
        () => waitForNoRunningAppHost(180000, fixture.appHostPath),
        () => restoreE2eCliPathForE2E(),
        () => restoreWorkspaceCliPath(),
        () => restoreLaunchJson(launchJsonPath, originalLaunchJson),
        () => restorePrimaryWorkspaceAppHostSelection(),
    ], 'Linked-worktree AppHost lifecycle E2E cleanup failed.');
}

async function restorePrimaryWorkspaceAppHostSelection(): Promise<void> {
    restoreWorkspaceAppHostConfig();
    const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
    await executeE2eControlCommand({ name: 'refreshAppHosts' });
    await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
    await waitForSelectedWorkspaceAppHost(getPrimaryAppHostProjectPath(), 120000);
}

async function removeLinkedWorktreePaths(seedRepositoryPath: string, linkedWorktreePath: string): Promise<void> {
    const gitDirectoryPath = path.join(seedRepositoryPath, '.git');
    if (fs.existsSync(gitDirectoryPath)) {
        for (let attempt = 0; attempt < (process.platform === 'win32' ? 10 : 2) && fs.existsSync(linkedWorktreePath); attempt++) {
            const removal = await runProcess('git', ['worktree', 'remove', '--force', linkedWorktreePath], {
                cwd: seedRepositoryPath,
                timeoutMs: 30000,
                rejectOnNonZeroExit: false,
            }).catch(() => undefined);
            if (removal?.exitCode === 0 || !fs.existsSync(linkedWorktreePath)) {
                break;
            }

            await delay(250);
        }
    }

    await removePathWithRetry(linkedWorktreePath);
    if (fs.existsSync(gitDirectoryPath)) {
        await runProcess('git', ['worktree', 'prune', '--expire', 'now'], {
            cwd: seedRepositoryPath,
            timeoutMs: 30000,
            rejectOnNonZeroExit: false,
        }).catch(() => undefined);
    }
    await removePathWithRetry(seedRepositoryPath);

    assert.strictEqual(fs.existsSync(linkedWorktreePath), false, `Expected linked worktree cleanup to remove ${linkedWorktreePath}.`);
    assert.strictEqual(fs.existsSync(seedRepositoryPath), false, `Expected seed repository cleanup to remove ${seedRepositoryPath}.`);
}

async function removePathWithRetry(targetPath: string): Promise<void> {
    const maximumAttempts = process.platform === 'win32' ? 40 : 3;
    for (let attempt = 1; ; attempt++) {
        try {
            fs.rmSync(targetPath, { recursive: true, force: true });
            return;
        }
        catch (error) {
            if (attempt >= maximumAttempts) {
                throw error;
            }

            await delay(250);
        }
    }
}

function resolveGitMetadataPath(baseDirectory: string, value: string): string {
    return path.resolve(baseDirectory, value);
}

function writeLaunchJson(launchJsonPath: string, value: unknown): void {
    fs.mkdirSync(path.dirname(launchJsonPath), { recursive: true });
    fs.writeFileSync(launchJsonPath, JSON.stringify(value, undefined, 2));
}

function restoreLaunchJson(launchJsonPath: string, originalLaunchJson: string | undefined): void {
    if (originalLaunchJson === undefined) {
        fs.rmSync(launchJsonPath, { force: true });
        return;
    }

    fs.writeFileSync(launchJsonPath, originalLaunchJson);
}

async function waitForLinkedAppHostCliProcess(
    cliPid: number,
    appHostPath: string,
    timeoutMs: number,
    expectRootIsolation = true,
): Promise<ProcessEntry> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const cliProcess = await getProcessEntry(cliPid);
        if (cliProcess) {
            assertLinkedAppHostCliLaunchExpectation(cliProcess.arguments, appHostPath, getCliPath(), expectRootIsolation);
            return cliProcess;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for Aspire CLI process ${cliPid} to launch ${appHostPath}.`);
}

function assertLinkedAppHostCliLaunchExpectation(
    argumentsList: readonly string[],
    appHostPath: string,
    cliPath: string,
    expectRootIsolation: boolean,
    platform = process.platform,
): void {
    if (expectRootIsolation) {
        assertLinkedAppHostCliLaunch(argumentsList, appHostPath, cliPath, platform);
        return;
    }

    const formattedArguments = JSON.stringify(argumentsList);
    assert.ok(
        argumentsList.length > 0 && commandLineArgumentEquals(argumentsList[0], cliPath, platform),
        `Expected the current E2E CLI '${cliPath}' as argv[0] in: ${formattedArguments}`);

    const runIndex = argumentsList.indexOf('run', 1);
    assert.ok(runIndex > 0, `Expected exact 'run' after the CLI path in: ${formattedArguments}`);
    const separatorIndex = argumentsList.indexOf('--', runIndex + 1);
    const rootArgumentsEnd = separatorIndex >= 0 ? separatorIndex : argumentsList.length;

    const isolatedIndex = argumentsList.indexOf('--isolated', runIndex + 1);
    assert.ok(isolatedIndex < 0 || isolatedIndex >= rootArgumentsEnd, `Did not expect inferred root '--isolated' after 'run' in: ${formattedArguments}`);
    assert.strictEqual(
        argumentsList.slice(runIndex + 1, rootArgumentsEnd).some(argument => argument === '--isolated=false'),
        false,
        `Did not expect any root '--isolated=false' after 'run' in: ${formattedArguments}`);

    const startDebugSessionIndex = argumentsList.indexOf('--start-debug-session', runIndex + 1);
    assert.ok(startDebugSessionIndex > runIndex && startDebugSessionIndex < rootArgumentsEnd, `Expected exact root '--start-debug-session' after 'run' in: ${formattedArguments}`);

    const appHostIndex = argumentsList.indexOf('--apphost', startDebugSessionIndex + 1);
    assert.ok(appHostIndex > startDebugSessionIndex && appHostIndex < rootArgumentsEnd, `Expected exact root '--apphost' after '--start-debug-session' in: ${formattedArguments}`);
    assert.ok(
        appHostIndex + 1 < argumentsList.length &&
        commandLineArgumentEquals(argumentsList[appHostIndex + 1], appHostPath, platform),
        `Expected exact --apphost path '${appHostPath}' immediately after '--apphost' in: ${formattedArguments}`);
}

async function waitForExactLinkedAppHostCliProcess(
    cliPid: number,
    cliPath: string,
    appHostPath: string,
    appHostArguments: readonly string[],
    timeoutMs: number,
    expectRootIsolation = true,
): Promise<ProcessEntry> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const cliProcess = await getProcessEntry(cliPid);
        if (cliProcess) {
            if (shouldWrapWithCmd(cliPath)) {
                const expectedArguments = getExpectedLinkedAppHostCliProcessArguments(
                    cliPath,
                    appHostPath,
                    appHostArguments,
                    expectRootIsolation);
                assert.ok(
                    cliProcess.arguments.length >= 5 &&
                    cliProcess.arguments.slice(0, 5).every((argument, index) =>
                        commandLineArgumentEquals(argument, expectedArguments[index])),
                    `Expected the cmd.exe wrapper prefix ${JSON.stringify(expectedArguments.slice(0, 5))}, got ${JSON.stringify(cliProcess.arguments)}.`);
                assert.ok(
                    cliProcess.commandLine.toLowerCase().includes(expectedArguments[5].toLowerCase()),
                    `Expected the raw cmd.exe command line to contain ${JSON.stringify(expectedArguments[5])}, got ${JSON.stringify(cliProcess.commandLine)}.`);
            }
            else {
                assertExactLinkedAppHostCliLaunch(
                    cliProcess.arguments,
                    appHostPath,
                    cliPath,
                    appHostArguments,
                    expectRootIsolation);
            }
            return cliProcess;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for exact argv from Aspire CLI process ${cliPid} that launches ${appHostPath}.`);
}

function waitForCliFallbackAndLaunchInvocations(
    invocationLogPath: string,
    invocationCountBeforeLaunch: number,
    appHostPath: string,
    appHostArguments: readonly string[],
    expectRootIsolation = true,
): string[][] {
    const invocations = getCliWrapperInvocations(invocationLogPath).slice(invocationCountBeforeLaunch);
    const expectedRunInvocation = getExpectedLinkedAppHostCliArguments(appHostPath, appHostArguments, expectRootIsolation);
    const runInvocations = invocations.filter(invocation => invocation[0] === 'run');
    assert.strictEqual(runInvocations.length, 1, `Expected one run invocation, got ${JSON.stringify(runInvocations)}.`);
    assert.strictEqual(runInvocations[0].length, expectedRunInvocation.length);
    const appHostPathIndex = expectedRunInvocation.indexOf(appHostPath);
    assert.ok(runInvocations[0].every((argument, index) =>
        index === appHostPathIndex
            ? commandLineArgumentEquals(argument, expectedRunInvocation[index])
            : argument === expectedRunInvocation[index]));

    if (expectRootIsolation) {
        const configInfoIndex = invocations.findIndex(invocation =>
            JSON.stringify(invocation) === JSON.stringify(['config', 'info', '--json', '--nologo']));
        assert.ok(configInfoIndex >= 0, `Expected tokenless capability negotiation before launch: ${JSON.stringify(invocations)}.`);
        const versionFallbackIndex = invocations.findIndex(
            (invocation, index) => index > configInfoIndex && JSON.stringify(invocation) === JSON.stringify(['--version']));
        assert.ok(versionFallbackIndex > configInfoIndex, `Expected stable 13.2 version fallback after tokenless config info: ${JSON.stringify(invocations)}.`);
    }

    return invocations;
}

function getExpectedLinkedAppHostCliArguments(
    appHostPath: string,
    appHostArguments: readonly string[],
    expectRootIsolation = true,
): string[] {
    return [
        'run',
        ...(expectRootIsolation ? ['--isolated'] : []),
        '--start-debug-session',
        '--nologo',
        '--apphost',
        appHostPath,
        '--',
        ...appHostArguments,
    ];
}

function getExpectedLinkedAppHostCliProcessArguments(
    cliPath: string,
    appHostPath: string,
    appHostArguments: readonly string[],
    expectRootIsolation = true,
): string[] {
    const cliArguments = getExpectedLinkedAppHostCliArguments(appHostPath, appHostArguments, expectRootIsolation);
    if (shouldWrapWithCmd(cliPath)) {
        const spawnCommand = getCmdShimSpawnCommand(cliPath, cliArguments);
        return [spawnCommand.command, ...spawnCommand.args];
    }

    return [cliPath, ...cliArguments];
}

function assertExactLinkedAppHostCliLaunch(
    argumentsList: readonly string[],
    appHostPath: string,
    cliPath: string,
    appHostArguments: readonly string[],
    expectRootIsolation = true,
    platform = process.platform,
): void {
    const expectedArguments = getExpectedLinkedAppHostCliProcessArguments(
        cliPath,
        appHostPath,
        appHostArguments,
        expectRootIsolation);
    const appHostPathIndex = expectedArguments.indexOf(appHostPath);
    const pathArgumentIndexes = shouldWrapWithCmd(cliPath)
        ? [0]
        : [0, appHostPathIndex];
    const argumentsMatch = argumentsList.length === expectedArguments.length &&
        argumentsList.every((argument, index) =>
            pathArgumentIndexes.includes(index)
                ? commandLineArgumentEquals(argument, expectedArguments[index], platform)
                : argument === expectedArguments[index]);

    assert.ok(
        argumentsMatch,
        `Expected exact Aspire CLI argv ${JSON.stringify(expectedArguments)}, got ${JSON.stringify(argumentsList)}.`);
}

async function waitForAppHostArgvEvidence(evidencePath: string, timeoutMs: number): Promise<string[]> {
    const started = Date.now();
    let lastError: unknown;
    while (Date.now() - started < timeoutMs) {
        try {
            const value = JSON.parse(fs.readFileSync(evidencePath, 'utf8')) as unknown;
            if (Array.isArray(value) && value.every(item => typeof item === 'string')) {
                return value;
            }

            lastError = new Error(`Expected a JSON string array, got ${JSON.stringify(value)}.`);
        }
        catch (error) {
            lastError = error;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for exact AppHost argv evidence at ${evidencePath}. Last error: ${String(lastError)}`);
}

async function waitForLinkedAppHostSpawnLog(appHostPath: string, timeoutMs: number): Promise<ExtensionSpawnLog> {
    const runRoot = getRunRoot();
    assert.ok(runRoot, 'ASPIRE_EXTENSION_E2E_RUN_ROOT is required to inspect Aspire Extension.log.');
    const logsRoot = path.join(runRoot, 'storage', 'settings', 'logs');
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        for (const logPath of findFilesNamed(logsRoot, 'Aspire Extension.log')) {
            const lines = fs.readFileSync(logPath, 'utf8').split(/\r?\n/);
            const line = [...lines].reverse().find(candidate =>
                candidate.includes('Spawning Aspire CLI process:') &&
                candidate.includes('--start-debug-session') &&
                commandLineTextIncludes(candidate, `--apphost ${appHostPath}`) &&
                candidate.includes('; cwd='));
            if (line) {
                return { path: logPath, line };
            }
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for Aspire Extension.log to record the linked AppHost launch for ${appHostPath}.`);
}

function commandLineTextIncludes(value: string, expected: string): boolean {
    return process.platform === 'win32'
        ? value.toLowerCase().includes(expected.toLowerCase())
        : value.includes(expected);
}

function findFilesNamed(rootPath: string, fileName: string): string[] {
    if (!fs.existsSync(rootPath)) {
        return [];
    }

    return fs.readdirSync(rootPath, { withFileTypes: true }).flatMap(entry => {
        const entryPath = path.join(rootPath, entry.name);
        if (entry.isDirectory()) {
            return findFilesNamed(entryPath, fileName);
        }

        return entry.isFile() && entry.name === fileName ? [entryPath] : [];
    });
}

function writeLinkedWorktreeArtifact(scenario: 'lm' | 'f5' | 'direct', artifact: Record<string, unknown>): void {
    const artifactPath = path.join(ensureDiagnosticsDir(), `apphost-lifecycle-linked-worktree-${scenario}.json`);
    fs.writeFileSync(artifactPath, JSON.stringify(artifact, undefined, 2));
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function startExternalAppHost(appHostPath: string): ExternalAppHostRun {
    const spawnCommand = getExternalCliSpawnCommand(getCliPath(), ['run', '--non-interactive', '--nologo', '--apphost', appHostPath]);
    const child = spawn(spawnCommand.command, spawnCommand.args, {
        cwd: getWorkspaceRoot(),
        env: process.env,
        shell: false,
        // `aspire stop` can signal the AppHost's Windows console process group. Keep the
        // test-owned AppHost in its own group so stopping it cannot terminate VS Code or
        // the E2E runner that launched it.
        detached: true,
        windowsVerbatimArguments: spawnCommand.windowsVerbatimArguments,
    });
    let stdout = '';
    let stderr = '';
    let completionResult: { exitCode: number | null; signal: NodeJS.Signals | null } | undefined;
    let completionError: Error | undefined;
    const completion = new Promise<{ exitCode: number | null; signal: NodeJS.Signals | null }>((resolve, reject) => {
        child.stdout.on('data', chunk => stdout = appendBoundedOutput(stdout, chunk.toString()));
        child.stderr.on('data', chunk => stderr = appendBoundedOutput(stderr, chunk.toString()));
        child.once('error', error => {
            completionError = new Error(`Failed to start external Aspire CLI: ${error.message}`);
            reject(completionError);
        });
        child.once('exit', (exitCode, signal) => {
            completionResult = { exitCode, signal };
            resolve(completionResult);
        });
    });
    completion.catch(() => undefined);
    return {
        child,
        completion,
        getCompletion: () => ({ result: completionResult, error: completionError }),
        getOutput: () => ({ stdout, stderr }),
    };
}

function getExternalCliSpawnCommand(command: string, args: string[]): { command: string; args: string[]; windowsVerbatimArguments?: boolean } {
    if (process.platform !== 'win32' || !/\.(?:cmd|bat)$/i.test(command)) {
        return { command, args };
    }

    const wrappedCommand = `"${[command, ...args].map(quoteCmdArgument).join(' ')}"`;
    return {
        command: process.env.ComSpec ?? 'cmd.exe',
        args: ['/d', '/v:off', '/s', '/c', wrappedCommand],
        windowsVerbatimArguments: true,
    };
}

function quoteCmdArgument(value: string): string {
    let quotedValue = '';
    let backslashCount = 0;
    for (const character of value) {
        if (character === '\\') {
            backslashCount++;
        }
        else if (character === '"') {
            quotedValue += '\\'.repeat(backslashCount * 2) + '""';
            backslashCount = 0;
        }
        else {
            quotedValue += '\\'.repeat(backslashCount) + character;
            backslashCount = 0;
        }
    }

    return `"${quotedValue}${'\\'.repeat(backslashCount * 2)}"`;
}

async function waitForExternalAppHost(externalRun: ExternalAppHostRun, appHostPath: string, timeoutMs: number): Promise<number> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const completion = externalRun.getCompletion();
        if (completion.error) {
            throw completion.error;
        }
        if (completion.result) {
            const output = externalRun.getOutput();
            throw new Error(`External Aspire CLI exited before its AppHost was discovered (exitCode=${completion.result.exitCode}, signal=${completion.result.signal}).\nstdout:\n${output.stdout}\nstderr:\n${output.stderr}`);
        }

        const runningAppHost = findRunningAppHost(readStateFile().state, appHostPath);
        if (runningAppHost?.appHostPid !== undefined) {
            return runningAppHost.appHostPid;
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for the external AppHost '${appHostPath}' to be discovered.`);
}

async function waitForChildProcessExit(externalRun: ExternalAppHostRun, timeoutMs: number): Promise<void> {
    let timeout: NodeJS.Timeout | undefined;
    try {
        await Promise.race([
            externalRun.completion,
            new Promise<never>((_, reject) => timeout = setTimeout(() => reject(new Error(`Timed out after ${timeoutMs}ms waiting for external Aspire CLI process ${externalRun.child.pid} to exit.`)), timeoutMs)),
        ]);
    }
    finally {
        if (timeout) {
            clearTimeout(timeout);
        }
    }
}

function appendBoundedOutput(current: string, next: string, maximumLength = 16 * 1024): string {
    const combined = current + next;
    return combined.length <= maximumLength ? combined : combined.slice(-maximumLength);
}

async function waitForProcessExit(pid: number, timeoutMs: number): Promise<void> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        if (!isProcessRunning(pid)) {
            return;
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for external AppHost process ${pid} to exit.`);
}

function isProcessRunning(pid: number): boolean {
    if (process.platform === 'linux') {
        try {
            // /proc/<pid>/stat starts with "<pid> (<command>) <state>"; Z and X are no longer running.
            const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8');
            const state = stat.slice(stat.lastIndexOf(') ') + 2, stat.lastIndexOf(') ') + 3);
            return state !== 'Z' && state !== 'X';
        }
        catch (error) {
            return !(error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT');
        }
    }

    if (process.platform === 'darwin') {
        // `ps -o stat=` emits a state such as S+ or Z; a zombie has terminated but awaits reaping.
        const result = spawnSync('/bin/ps', ['-o', 'stat=', '-p', String(pid)], { encoding: 'utf8' });
        if (result.error) {
            throw result.error;
        }
        if (result.status === null) {
            throw new Error(`Unable to determine process state for PID ${pid}.`);
        }
        return result.status === 0 && !result.stdout.trimStart().startsWith('Z');
    }

    if (process.platform === 'win32') {
        const result = spawnSync('powershell.exe', [
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            `$process = Get-Process -Id ${pid} -ErrorAction SilentlyContinue; if ($null -ne $process -and -not $process.HasExited) { exit 0 } else { exit 1 }`,
        ], { windowsHide: true });
        if (result.error) {
            throw result.error;
        }
        if (result.status === null) {
            throw new Error(`Unable to determine process state for PID ${pid}.`);
        }
        return result.status === 0;
    }

    try {
        process.kill(pid, 0);
        return true;
    }
    catch (error) {
        return !(error && typeof error === 'object' && 'code' in error && error.code === 'ESRCH');
    }
}

function writeTimeoutCliWrapper(): { cliPath: string; pidPath: string; directory: string } {
    const directory = path.join(getWorkspaceRoot(), '.e2e-cli-wrappers', 'run-aspire-cli-timeout');
    const childScriptPath = path.join(directory, 'timeout-child.js');
    const pidPath = path.join(directory, 'timeout-child.pid');
    const configInfo = JSON.stringify({
        localSettingsPath: path.join(getWorkspaceRoot(), 'aspire.config.json'),
        globalSettingsPath: path.join(getWorkspaceRoot(), 'global-aspire.config.json'),
        availableFeatures: [],
        localSettingsSchema: { properties: [] },
        globalSettingsSchema: { properties: [] },
        capabilities: [],
    });
    fs.rmSync(directory, { recursive: true, force: true });
    fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(childScriptPath, [
        "const fs = require('fs');",
        "const { spawn } = require('child_process');",
        'const [pidPath, mode, ...args] = process.argv.slice(2);',
        "if (mode === 'config' && args[0] === 'info' && args.includes('--json')) {",
        `  console.log(${JSON.stringify(configInfo)});`,
        '  process.exit(0);',
        '}',
        "const output = args.join(' ');",
        'console.log(output);',
        'console.error(output);',
        "if (mode === 'fail') {",
        '  process.exit(23);',
        '}',
        "if (mode !== 'timeout-tree') {",
        '  process.exit(0);',
        '}',
        "const descendant = spawn(process.execPath, ['-e', 'setInterval(() => undefined, 1000);'], { stdio: 'ignore' });",
        'fs.writeFileSync(pidPath, String(descendant.pid));',
        'setInterval(() => undefined, 1000);',
        '',
    ].join('\n'));

    if (process.platform === 'win32') {
        const cliPath = path.join(directory, 'aspire-timeout.cmd');
        fs.writeFileSync(cliPath, [
            '@echo off',
            'if "%~1"=="--version" (',
            '  echo 99.0.0',
            '  exit /b 0',
            ')',
            `${quoteWindowsBatchArgument(process.execPath)} ${quoteWindowsBatchArgument(childScriptPath)} ${quoteWindowsBatchArgument(pidPath)} %*`,
            '',
        ].join('\r\n'));
        return { cliPath, pidPath, directory };
    }

    const cliPath = path.join(directory, 'aspire-timeout');
    fs.writeFileSync(cliPath, [
        '#!/bin/sh',
        'if [ "$1" = "--version" ]; then',
        '  echo "99.0.0"',
        '  exit 0',
        'fi',
        `exec ${quotePosixShellArgument(process.execPath)} ${quotePosixShellArgument(childScriptPath)} ${quotePosixShellArgument(pidPath)} "$@"`,
        '',
    ].join('\n'), { mode: 0o755 });
    return { cliPath, pidPath, directory };
}

function quotePosixShellArgument(value: string): string {
    return `'${value.replace(/'/g, "'\\''")}'`;
}

function quoteWindowsBatchArgument(value: string): string {
    return `"${value.replace(/%/g, '%%')}"`;
}

async function waitForProcessIdFile(filePath: string, timeoutMs: number): Promise<number> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        if (fs.existsSync(filePath)) {
            const pid = Number.parseInt(fs.readFileSync(filePath, 'utf8'), 10);
            if (Number.isInteger(pid) && pid > 0) {
                return pid;
            }
        }

        await delay(100);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for process ID evidence at ${filePath}.`);
}

async function captureError(action: () => Promise<unknown>): Promise<Error> {
    try {
        await action();
    }
    catch (error) {
        return error instanceof Error ? error : new Error(String(error));
    }

    throw new Error('Expected the operation to fail.');
}

async function invokeControlCommand<T>(command: Parameters<typeof executeE2eControlCommand>[0], timeoutMs = 120000): Promise<T> {
    const status = await executeE2eControlCommand(command, { timeoutMs });
    if (status.errorMessage) {
        throw new Error(`E2E control command '${command.name}' failed: ${status.errorMessage}`);
    }

    return status.result as T;
}

/**
 * Invokes a lifecycle tool and accepts the confirmation VS Code raises for each
 * invocation. `vscode.lm.invokeTool` blocks on that modal, so the control command must
 * be started before the dialogs are answered rather than awaited first.
 */
async function invokeLifecycleTool(
    command: Parameters<typeof executeE2eControlCommand>[0],
    timeoutMs: number,
    expectedConfirmations: number,
    screenshotName?: string
): Promise<{ results: LifecycleToolResult[]; dialogs: ModalDialogInteraction[] }> {
    const invocation = invokeControlCommand<LanguageModelToolInvocationResponse>(command, timeoutMs);
    // Keep the rejection observed while the dialogs are being answered; the real failure
    // is reported when the invocation is awaited below.
    invocation.catch(() => undefined);

    const dialogs: ModalDialogInteraction[] = [];
    for (let index = 0; index < expectedConfirmations; index++) {
        dialogs.push(await interactWithModalDialog('Yes', 180000, index === 0 ? screenshotName : undefined));
    }

    const result = await invocation;
    assertInvocationCompleted(result);
    return { results: result.results.map(item => JSON.parse(item) as LifecycleToolResult), dialogs };
}

async function invokeToolWithoutConfirmation<T>(
    command: Parameters<typeof executeE2eControlCommand>[0],
    timeoutMs = 120000): Promise<T> {
    const response = await invokeLanguageModelTool(command, timeoutMs);
    assertInvocationCompleted(response);
    assert.strictEqual(response.results.length, 1);
    return JSON.parse(response.results[0]) as T;
}

/**
 * Re-invokes a read-only tool until its result satisfies `isSettled`.
 *
 * Resource projections report whatever the CLI has published so far, so a single call can observe
 * a resource mid-startup. Polling the tool is the only condition available here: the E2E state file
 * projects neither health status nor the bounded resource shape the tool returns.
 */
async function waitForToolResult<T extends Record<string, unknown>>(
    command: Parameters<typeof executeE2eControlCommand>[0],
    isSettled: (result: T) => boolean,
    description: string,
    timeoutMs = 120000): Promise<T> {
    const started = Date.now();
    let lastResult: T | undefined;
    while (Date.now() - started < timeoutMs) {
        lastResult = await invokeToolWithoutConfirmation<T>(command);
        if (isSettled(lastResult)) {
            return lastResult;
        }

        await new Promise(resolve => setTimeout(resolve, 500));
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}. Last result: ${JSON.stringify(lastResult)}`);
}

async function invokeLanguageModelTool(
    command: Parameters<typeof executeE2eControlCommand>[0],
    timeoutMs = 120000): Promise<LanguageModelToolInvocationResponse> {
    const response = await invokeControlCommand<LanguageModelToolInvocationResponse>(command, timeoutMs);
    return {
        results: response.results,
        cancellations: response.cancellations,
        unexpectedFailures: response.unexpectedFailures,
    };
}

async function invokeLanguageModelToolWithConfirmations<T>(
    command: Parameters<typeof executeE2eControlCommand>[0],
    timeoutMs: number,
    expectedConfirmations: number,
    screenshotName?: string,
    buttonTitle = 'Yes'
): Promise<{ results: T[]; cancellations: number; unexpectedFailures: number; dialogs: ModalDialogInteraction[] }> {
    const invocation = invokeLanguageModelTool(command, timeoutMs);
    invocation.catch(() => undefined);

    const dialogs: ModalDialogInteraction[] = [];
    for (let index = 0; index < expectedConfirmations; index++) {
        dialogs.push(await interactWithModalDialog(buttonTitle, 180000, index === 0 ? screenshotName : undefined));
    }

    const response = await invocation;
    return {
        results: response.results.map(result => JSON.parse(result) as T),
        cancellations: response.cancellations,
        unexpectedFailures: response.unexpectedFailures,
        dialogs,
    };
}

function assertSafeEditorAssistanceResult(result: unknown): void {
    const forbiddenKeys = new Set([
        'args',
        'arguments',
        'content',
        'dashboardurl',
        'debugconfig',
        'debugconfiguration',
        'env',
        'environment',
        'error',
        'errormessage',
        'logcontent',
        'logs',
        'outputcontent',
        'pid',
        'processid',
        'rawerror',
        'sessionid',
        'url',
        'urls',
    ]);
    const workspaceRoot = getWorkspaceRoot();

    const visit = (value: unknown, location: string): void => {
        if (typeof value === 'string') {
            assert.strictEqual(value.includes(workspaceRoot), false, `${location} contained the absolute workspace path.`);
            assert.doesNotMatch(value, /https?:\/\//i, `${location} contained a URL.`);
            return;
        }

        if (Array.isArray(value)) {
            value.forEach((item, index) => visit(item, `${location}[${index}]`));
            return;
        }

        if (value && typeof value === 'object') {
            for (const [key, child] of Object.entries(value)) {
                const normalizedKey = key.replaceAll('_', '').replaceAll('-', '').toLowerCase();
                assert.strictEqual(
                    forbiddenKeys.has(normalizedKey) ||
                    normalizedKey.endsWith('pid') ||
                    normalizedKey.endsWith('processid') ||
                    normalizedKey.endsWith('sessionid') ||
                    normalizedKey.includes('debugconfig'),
                    false,
                    `${location} contained forbidden '${key}' data.`);
                visit(child, `${location}.${key}`);
            }
        }
    };

    visit(result, 'editorAssistance');
}

function assertDeniedToolInvocation(
    invocation: { results: Array<Record<string, unknown>>; cancellations: number; unexpectedFailures: number },
    toolName: string
): void {
    assert.strictEqual(invocation.unexpectedFailures, 0);
    if (invocation.cancellations > 0) {
        assert.deepStrictEqual(invocation.results, []);
        assert.strictEqual(invocation.cancellations, 1);
        return;
    }

    assert.deepStrictEqual(invocation.results, [{
        success: false,
        tool: toolName,
        outcome: 'canceled',
    }]);
}

function assertInvocationCompleted(invocation: { cancellations: number; unexpectedFailures: number }): void {
    assert.strictEqual(invocation.cancellations, 0);
    assert.strictEqual(invocation.unexpectedFailures, 0);
}

function assertSafeLifecycleArtifact(artifact: Record<string, unknown>): void {
    assertSafeEditorAssistanceResult(artifact);
    const serialized = JSON.stringify(artifact);
    for (const forbidden of ['--start-debug-session', 'stdout', 'stderr']) {
        assert.strictEqual(
            serialized.includes(forbidden),
            false,
            `Editor-assistance artifact contained forbidden '${forbidden}' data.`);
    }
}

async function waitForAppHostProcessCount(appHostPath: string, expectedCount: number, timeoutMs: number): Promise<number[]> {
    const started = Date.now();
    let pids: number[] = [];
    while (Date.now() - started < timeoutMs) {
        pids = await findAppHostProcessIds(appHostPath);
        if (pids.length === expectedCount) {
            return pids;
        }

        await new Promise(resolve => setTimeout(resolve, 500));
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${expectedCount} AppHost process(es) for ${appHostPath}. Found: ${JSON.stringify(pids)}`);
}

/**
 * Counts the operating system processes the editor owns for an AppHost. The extension
 * launches the CLI with `run --start-debug-session ... --apphost <path>`, so matching the
 * AppHost path in the command line finds exactly the process the lifecycle tools created.
 * The OS is used instead of the `aspire ps` view state because that view only reflects the
 * polled tree model, which is not an authoritative statement about running processes.
 */
async function findAppHostProcessIds(appHostPath: string): Promise<number[]> {
    const processes = await listProcessEntries('--start-debug-session');

    return processes
        .filter(entry => commandLineHasExactAppHost(entry.arguments, appHostPath))
        .map(entry => entry.pid)
        .sort((left, right) => left - right);
}

function commandLineHasExactAppHost(argumentsList: readonly string[], appHostPath: string, platform = process.platform): boolean {
    const startDebugSessionIndex = argumentsList.indexOf('--start-debug-session');
    const appHostIndex = argumentsList.indexOf('--apphost', startDebugSessionIndex + 1);
    return startDebugSessionIndex >= 0 &&
        appHostIndex > startDebugSessionIndex &&
        appHostIndex + 1 < argumentsList.length &&
        commandLineArgumentEquals(argumentsList[appHostIndex + 1], appHostPath, platform);
}

function writeLifecycleToolArtifact(artifact: Record<string, unknown>): void {
    assertSafeLifecycleArtifact(artifact);
    const artifactPath = path.join(ensureDiagnosticsDir(), 'apphost-lifecycle-language-model-tools.json');
    fs.writeFileSync(artifactPath, JSON.stringify(artifact, undefined, 2));
}
