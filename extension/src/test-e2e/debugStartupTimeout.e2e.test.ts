import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, waitForCommandOutcome, waitForDebugDashboardUrl, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, stopPrimaryAppHostIfRunning, writeFileWithRetry } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getRunRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire debug startup timeout E2E', function () {
    this.timeout(360000);

    const delayStartMarker = 'TIMEOUT_E2E_DELAY_START';
    const delayEndMarker = 'TIMEOUT_E2E_DELAY_END';

    teardown(async () => {
        if (!shouldRunStartupTimeoutProof()) {
            return;
        }

        await runE2eTeardown([
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'Debug startup timeout E2E teardown failed.');
    });

    test('debug AppHost startup waits past the old 60 second backchannel timeout', async function () {
        if (!shouldRunStartupTimeoutProof()) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const appHostSourcePath = path.join(path.dirname(appHostPath), 'AppHost.cs');
        const originalSource = fs.readFileSync(appHostSourcePath, 'utf8');

        try {
            const delayedSource = originalSource.replace(
                'var builder = DistributedApplication.CreateBuilder(args);',
                `System.Console.WriteLine("${delayStartMarker} " + System.DateTimeOffset.UtcNow.ToString("O"));\nSystem.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(65));\nSystem.Console.WriteLine("${delayEndMarker} " + System.DateTimeOffset.UtcNow.ToString("O"));\nvar builder = DistributedApplication.CreateBuilder(args);`);
            assert.notStrictEqual(delayedSource, originalSource, 'Expected AppHost fixture to contain DistributedApplication.CreateBuilder(args).');
            writeFileWithRetry(appHostSourcePath, delayedSource);

            const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
            await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);

            await waitForDebugSessionStartup(appHostPath, 240000);
            await waitForDebugDashboardUrl(appHostPath, 240000);

            const evidence = await waitForStartupDelayEvidence(delayStartMarker, delayEndMarker, 120000);
            assert.ok(evidence.delayMs >= 60000, `Expected AppHost delay to cross the old 60 second backchannel timeout. Actual delay: ${evidence.delayMs}ms.`);
            assert.ok(!evidence.logContent.includes('Timed out waiting'), 'Expected debug startup to complete without a timeout.');

            const expectedTimeout = '86400';
            assert.strictEqual(process.env.ASPIRE_CLI_START_TIMEOUT, undefined, 'Expected this E2E proof to leave ASPIRE_CLI_START_TIMEOUT unset so the extension debug launch must configure it.');
            const extensionLogs = readExtensionLogs();
            assert.ok(extensionLogs.includes('run --start-debug-session'), 'Expected extension logs to include the debug AppHost CLI launch.');
            assert.ok(extensionLogs.includes(`ASPIRE_CLI_START_TIMEOUT=${expectedTimeout}`), `Expected extension-spawned CLI to use ASPIRE_CLI_START_TIMEOUT=${expectedTimeout}.`);
        }
        finally {
            await runE2eTeardown([
                () => writeFileWithRetry(appHostSourcePath, originalSource),
                () => executeE2eControlCommand({ name: 'stopDebugging' }),
                () => waitForNoDebugSessions().catch(() => undefined),
            ], 'Debug startup timeout AppHost source cleanup failed.');
        }
    });
});

function shouldRunStartupTimeoutProof(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_UNSET_CLI_START_TIMEOUT === 'true';
}

async function waitForStartupDelayEvidence(startMarker: string, endMarker: string, timeoutMs: number): Promise<{ delayMs: number; logContent: string }> {
    const started = Date.now();
    let lastLogContent = '';
    while (Date.now() - started < timeoutMs) {
        const logContent = readCliLogs();
        lastLogContent = logContent;

        const start = tryGetMarkerTimestamp(logContent, startMarker);
        const end = tryGetMarkerTimestamp(logContent, endMarker);
        if (start !== undefined && end !== undefined) {
            return {
                delayMs: end.getTime() - start.getTime(),
                logContent,
            };
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for CLI log markers '${startMarker}' and '${endMarker}'. Last log content:\n${lastLogContent}`);
}

function readCliLogs(): string {
    const runRoot = getRunRoot();
    assert.ok(runRoot, 'ASPIRE_EXTENSION_E2E_RUN_ROOT is required to find isolated CLI logs.');

    const logsRoot = path.join(runRoot, 'aspire-home', 'logs');
    if (!fs.existsSync(logsRoot)) {
        return '';
    }

    return fs.readdirSync(logsRoot, { withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.log'))
        .map(entry => fs.readFileSync(path.join(logsRoot, entry.name), 'utf8'))
        .join('\n');
}

function readExtensionLogs(): string {
    const runRoot = getRunRoot();
    assert.ok(runRoot, 'ASPIRE_EXTENSION_E2E_RUN_ROOT is required to find isolated extension logs.');

    const logsRoot = path.join(runRoot, 'storage', 'settings', 'logs');
    if (!fs.existsSync(logsRoot)) {
        return '';
    }

    return readFilesRecursively(logsRoot, 'Aspire Extension.log').join('\n');
}

function readFilesRecursively(directory: string, fileName: string): string[] {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const entryPath = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            return readFilesRecursively(entryPath, fileName);
        }

        return entry.isFile() && entry.name === fileName ? [fs.readFileSync(entryPath, 'utf8')] : [];
    });
}

function tryGetMarkerTimestamp(logContent: string, marker: string): Date | undefined {
    // AppHost console output is captured by the CLI log as:
    //   [2026-06-20 04:55:00.029] [INFO] [AppHost] TIMEOUT_E2E_DELAY_START 2026-06-20T04:55:00.0252790+00:00
    // Parse the explicit marker timestamp instead of the log prefix so the measured delay comes
    // from the delayed AppHost process, not from CLI log flushing.
    const match = new RegExp(`${marker} ([^\\s]+)`).exec(logContent);
    return match ? new Date(normalizeDotNetTimestamp(match[1])) : undefined;
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function normalizeDotNetTimestamp(value: string): string {
    return value.replace(/(\.\d{3})\d+([+-]\d\d:\d\d|Z)$/, '$1$2');
}
