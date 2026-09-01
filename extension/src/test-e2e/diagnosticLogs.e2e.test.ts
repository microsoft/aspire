import * as assert from 'assert';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForRepositoryIdle, waitForRunningAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { dismissAllNotifications, getNotificationMessages, openAspireView, takeNotificationAction, waitForEditorTitle, waitForNotificationMessage } from './helpers/vscode';

interface CliResult {
    exitCode: number | null;
    stdout: string;
    stderr: string;
}

interface OpenEditor {
    uri?: string;
    isPreview: boolean;
}

suite('CLI diagnostic log actions E2E', function () {
    this.timeout(420000);

    teardown(async () => {
        await runE2eTeardown([
            () => stopPrimaryAppHostIfRunning(),
            () => executeE2eControlCommand({ name: 'closeAllEditors' }),
            () => dismissAllNotifications(),
        ], 'CLI diagnostic log actions E2E teardown failed.');
    });

    test('opens the current CLI log selected from the failure notification', async function () {
        if (process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true') {
            this.skip();
        }

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

    test('offers both current CLI and AppHost logs on a connected failure', async function () {
        if (process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true') {
            this.skip();
        }

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
