import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForExtensionState, waitForRepositoryIdle, waitForRunningAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, getCliWrapperInvocations, getPsFollowProcesses, getWorkspaceSettingsPath, restoreE2eCliPathForE2E, runE2eTeardown, setE2eCliPathForE2E, stopPrimaryAppHostIfRunning, writeTrackedDelayedPsCliWrapper, writeWorkspaceSetting } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getRunRoot, getWorkspaceRoot } from './helpers/paths';
import { dismissAllNotifications, getNotificationMessages, hideAspireView, openAspireView, takeNotificationAction, waitForEditorTitle, waitForNotificationMessage } from './helpers/vscode';

interface CliResult {
    exitCode: number | null;
    stdout: string;
    stderr: string;
}

interface OpenEditor {
    uri?: string;
    isPreview: boolean;
}

const currentCliTest = process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true' ? test.skip : test;

suite('CLI diagnostic log actions E2E', function () {
    this.timeout(420000);

    teardown(async () => {
        await runE2eTeardown([
            () => stopPrimaryAppHostIfRunning(),
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => dismissAllNotifications(),
        ], 'CLI diagnostic log actions E2E teardown failed.');
    });

    currentCliTest('status polling keeps diagnostic file count and disk usage bounded and stops when Aspire UI is hidden', async function () {
        await openAspireView();
        await waitForWorkspaceAppHost();
        await waitForRepositoryIdle();

        const pollingSetting = 'aspire.appHostsPollingInterval';
        const debugLoggingSetting = 'aspire.enableAspireCliDebugLogging';
        const settings = JSON.parse(fs.readFileSync(getWorkspaceSettingsPath(), 'utf8')) as Record<string, unknown>;
        const runRoot = getRunRoot();
        assert.ok(runRoot, 'An isolated E2E run root is required.');
        const logsDirectory = path.join(runRoot, 'aspire-home', 'logs');
        const logFiles = () => fs.existsSync(logsDirectory) ? fs.readdirSync(logsDirectory).filter(file => file.endsWith('.log')).sort() : [];
        const assertLogBounds = () => {
            const files = logFiles();
            assert.ok(files.length <= 100, `Expected at most 100 automatic CLI logs, got ${files.length}.`);
            // Retention can remove an inactive file between enumeration and stat.
            const sizes = files.map(file => fs.statSync(path.join(logsDirectory, file), { throwIfNoEntry: false })?.size ?? 0);
            assert.ok(sizes.every(size => size <= 1024 * 1024), 'Each automatic CLI log must be at most 1 MiB.');
            assert.ok(sizes.reduce((sum, size) => sum + size, 0) <= 100 * 1024 * 1024, 'Automatic CLI logs must use at most 100 MiB.');
        };
        const wrapper = writeTrackedDelayedPsCliWrapper(0);
        const statusSpawns = () => getCliWrapperInvocations(wrapper.invocationLogPath).filter(args => args[0] === 'ps').length;

        try {
            await setE2eCliPathForE2E(wrapper.cliPath);
            writeWorkspaceSetting(debugLoggingSetting, false);
            writeWorkspaceSetting(pollingSetting, 1000);
            fs.mkdirSync(logsDirectory, { recursive: true });
            for (let index = 0; index < 150; index++) {
                const file = path.join(logsDirectory, `cli_20000101T000000_${index.toString(16).padStart(8, '0')}.log`);
                fs.writeFileSync(file, `old-${index}`);
                fs.utimesSync(file, 0, index);
            }
            const oversizedFile = path.join(logsDirectory, 'cli_19990101T000000_00000000.log');
            fs.writeFileSync(oversizedFile, '');
            fs.truncateSync(oversizedFile, 1024 * 1024 + 1);
            await executeE2eControlCommand({ name: 'openFile', filePath: getPrimaryAppHostProjectPath() });
            await waitForExtensionState(() => getPsFollowProcesses().some(process => !process.exited), 'status follower to start');
            await waitForRepositoryIdle();
            await waitForExtensionState(() => logFiles().length <= 100 && !fs.existsSync(oversizedFile), 'automatic CLI log retention to prune existing files');
            assertLogBounds();
            const activeFiles = new Set(logFiles());
            const activeSpawns = statusSpawns();
            for (let interval = 0; interval < 4; interval++) {
                await new Promise(resolve => setTimeout(resolve, 1000));
                assertLogBounds();
                assert.strictEqual(getPsFollowProcesses().filter(process => !process.exited).length, 1);
            }
            assert.ok(statusSpawns() > activeSpawns, 'Authoritative queries must retain the configured cadence while following.');
            assert.ok(logFiles().some(file => !activeFiles.has(file)), 'Background queries must retain file-backed diagnostics.');

            await hideAspireView();
            assert.ok(getPsFollowProcesses().some(process => !process.exited), 'The visible AppHost editor still needs live data.');

            await executeE2eControlCommand({ name: 'openFile', filePath: getWorkspaceSettingsPath() });
            await waitForExtensionState(() => getPsFollowProcesses().every(process => process.exited), 'all status followers to exit');
            // Allow a one-shot query already in flight during the visibility change to finish.
            await new Promise(resolve => setTimeout(resolve, 2000));
            const idleFiles = logFiles();
            const idleSpawns = statusSpawns();
            for (let interval = 0; interval < 4; interval++) {
                await new Promise(resolve => setTimeout(resolve, 1000));
                assert.deepStrictEqual(logFiles(), idleFiles, 'Hidden Aspire UI must not keep accumulating CLI logs.');
                assertLogBounds();
                assert.strictEqual(statusSpawns(), idleSpawns, 'Hidden Aspire UI must not keep spawning status queries.');
            }

            await openAspireView();
            await waitForExtensionState(() => getPsFollowProcesses().some(process => !process.exited), 'status watching to resume');
        } finally {
            await restoreE2eCliPathForE2E();
            writeWorkspaceSetting(pollingSetting, settings[pollingSetting]);
            writeWorkspaceSetting(debugLoggingSetting, settings[debugLoggingSetting]);
        }
    });

    test('background debug logging retains file-backed diagnostics', async function () {
        await openAspireView();
        await waitForWorkspaceAppHost();
        await waitForRepositoryIdle();
        const setting = 'aspire.enableAspireCliDebugLogging';
        const settings = JSON.parse(fs.readFileSync(getWorkspaceSettingsPath(), 'utf8')) as Record<string, unknown>;
        const runRoot = getRunRoot();
        assert.ok(runRoot, 'An isolated E2E run root is required.');
        const logsDirectory = path.join(runRoot, 'aspire-home', 'logs');
        const logFiles = () => fs.existsSync(logsDirectory) ? fs.readdirSync(logsDirectory).filter(file => file.endsWith('.log')) : [];
        const previousFiles = new Set(logFiles());
        try {
            writeWorkspaceSetting(setting, true);
            await openAspireView();
            await waitForExtensionState(() => logFiles().some(file =>
                !previousFiles.has(file)
                && fs.readFileSync(path.join(logsDirectory, file), 'utf8').includes('Command: aspire ps --follow --format json')),
            'file diagnostics for the background status follower with debug logging enabled');
        } finally {
            writeWorkspaceSetting(setting, settings[setting]);
        }
    });

    currentCliTest('opens the current CLI log selected from the failure notification', async function () {
        await openAspireView();
        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'closeAllEditors' });
        await dismissAllNotifications();

        const missingAppHost = path.join(getWorkspaceRoot(), 'missing', 'Missing.AppHost.csproj');
        const failureText = 'The --apphost option specified a project that does not exist';
        const result = await runFailingCliWithNotification(
            ['run', '--apphost', missingAppHost, '--non-interactive', '--nologo'],
            failureText);
        const cliLogPath = getDiagnosticLogPath(result.stderr, 'See logs at');

        await assertSingleFailureNotification(failureText);
        await takeNotificationAction(failureText, 'Open CLI Log');
        await assertPersistentLogEditor(cliLogPath);
    });

    currentCliTest('offers both current CLI and AppHost logs on a connected failure', async function () {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await executeE2eControlCommand({ name: 'closeAllEditors' });
        await dismissAllNotifications();

        const appHostPath = getPrimaryAppHostProjectPath();
        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);
        await waitForRunningAppHost(180000);

        const failureText = "Required option '--message' was not provided.";
        const firstFailure = await runConnectedFailure(appHostPath, failureText);
        const firstCliLogPath = getDiagnosticLogPath(firstFailure.stderr, 'See logs at');

        await assertSingleFailureNotification(failureText);
        await takeNotificationAction(failureText, 'Open CLI Log');
        await assertPersistentLogEditor(firstCliLogPath);

        await executeE2eControlCommand({ name: 'closeAllEditors' });
        await dismissAllNotifications();

        const secondFailure = await runConnectedFailure(appHostPath, failureText);
        const appHostLogPath = getDiagnosticLogPath(secondFailure.stderr, 'See AppHost logs at');

        await assertSingleFailureNotification(failureText);
        await takeNotificationAction(failureText, 'Open AppHost Log');
        await assertPersistentLogEditor(appHostLogPath);
    });
});

async function runConnectedFailure(appHostPath: string, failureText: string): Promise<CliResult> {
    return await runFailingCliWithNotification(
        ['resource', 'e2e-worker', 'echo-arguments', '--apphost', appHostPath, '--non-interactive', '--nologo'],
        failureText);
}

async function runFailingCliWithNotification(
    args: string[],
    failureText: string
): Promise<CliResult> {
    const [status] = await Promise.all([
        executeE2eControlCommand({
            name: 'runAspireCli',
            args,
            workingDirectory: '.',
            allowNonZeroExit: true,
        }, { timeoutMs: 180000 }),
        waitForNotificationMessage(failureText, 180000),
    ]);
    const result = status.result as CliResult;

    assert.notStrictEqual(result.exitCode, 0);
    return result;
}

function getDiagnosticLogPath(output: string, prefix: string): string {
    const match = new RegExp(`${prefix} (.+?\\.log)(?:\\r?\\n|$)`).exec(output);
    assert.ok(match, `Expected CLI stderr to contain '${prefix}' followed by a log path: ${JSON.stringify(output)}`);
    return match[1].trim();
}

async function assertSingleFailureNotification(failureText: string): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 500));
    const relevantNotifications = (await getNotificationMessages()).filter(message =>
        message.includes(failureText)
        || message.includes('See logs at')
        || message.includes('See AppHost logs at'));
    assert.strictEqual(relevantNotifications.length, 1, `Expected one actionable failure notification, got: ${JSON.stringify(relevantNotifications)}`);
}

async function assertPersistentLogEditor(expectedPath: string): Promise<void> {
    await waitForEditorTitle(path.basename(expectedPath), 60000);

    const status = await executeE2eControlCommand({ name: 'getOpenEditors' });
    const editors = (status.result as OpenEditor[]).filter(editor =>
        editor.uri?.startsWith('file:')
        && path.extname(fileURLToPath(editor.uri)) === '.log');
    const matchingEditors = editors.filter(editor =>
        editor.uri?.startsWith('file:')
        && isSamePath(fileURLToPath(editor.uri), expectedPath));

    assert.strictEqual(matchingEditors.length, 1, `Expected one editor for '${expectedPath}'.`);
    assert.strictEqual(matchingEditors[0].isPreview, false);
}
