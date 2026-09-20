import * as assert from 'assert';
import * as fs from 'fs';
import { getCommandInvocationCount, waitForCommandOutcome, waitForDebugConsoleOutput, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, writeFileWithRetry } from './helpers/fixtures';
import { getJavaAppHostSourcePath, prepareJavaWorkspace, waitForJavaLanguageServerImport } from './helpers/java';
import { readCliLogs, readExtensionLogs } from './helpers/logs';

suite('Aspire Java AppHost debug startup timeout E2E', function () {
    this.timeout(1800000);

    const delayStartMarker = 'JAVA_TIMEOUT_E2E_DELAY_START';
    const delayEndMarker = 'JAVA_TIMEOUT_E2E_DELAY_END';

    suiteSetup(async () => {
        await prepareJavaWorkspace();
        await executeE2eControlCommand({ name: 'openFile', filePath: getJavaAppHostSourcePath() });
        await waitForJavaLanguageServerImport();
    });

    test('debug startup waits past the old 60 second guest backchannel timeout', async function () {
        if (!shouldRunStartupTimeoutProof()) {
            this.skip();
        }

        const appHostSourcePath = getJavaAppHostSourcePath();
        const originalSource = fs.readFileSync(appHostSourcePath, 'utf8');

        try {
            const delayedSource = originalSource.replace(
                '    var builder = DistributedApplication.CreateBuilder();',
                `    System.out.println("${delayStartMarker} " + java.time.Instant.now());\n    Thread.sleep(65_000);\n    System.out.println("${delayEndMarker} " + java.time.Instant.now());\n    var builder = DistributedApplication.CreateBuilder();`);
            assert.notStrictEqual(delayedSource, originalSource, 'Expected the Java AppHost fixture to create its builder.');
            writeFileWithRetry(appHostSourcePath, delayedSource);

            const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
            await executeE2eControlCommand({ name: 'debugAppHost', appHostPath: appHostSourcePath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);

            const startOutput = await waitForDebugConsoleOutput(delayStartMarker, appHostSourcePath, 120000);
            const endOutput = await waitForDebugConsoleOutput(delayEndMarker, appHostSourcePath, 120000);
            await waitForDebugSessionStartup(appHostSourcePath, 240000);

            const delayMs = getMarkerTimestamp(endOutput.output, delayEndMarker).getTime()
                - getMarkerTimestamp(startOutput.output, delayStartMarker).getTime();
            assert.ok(delayMs >= 60000, `Expected the Java AppHost delay to cross the old 60 second timeout. Actual delay: ${delayMs}ms.`);

            const cliLogs = readCliLogs();
            assert.ok(!cliLogs.includes('Timed out waiting'), 'Expected Java debug startup to complete without a CLI timeout.');

            const expectedTimeout = '86400';
            assert.strictEqual(process.env.ASPIRE_CLI_START_TIMEOUT, undefined, 'Expected the Java E2E proof to leave ASPIRE_CLI_START_TIMEOUT unset.');
            const extensionLogs = readExtensionLogs();
            assert.ok(extensionLogs.includes('run --start-debug-session'), 'Expected extension logs to include the Java AppHost CLI launch.');
            assert.ok(extensionLogs.includes(`ASPIRE_CLI_START_TIMEOUT=${expectedTimeout}`), `Expected the extension-spawned CLI to use ASPIRE_CLI_START_TIMEOUT=${expectedTimeout}.`);
        }
        finally {
            await runE2eTeardown([
                () => writeFileWithRetry(appHostSourcePath, originalSource),
                () => executeE2eControlCommand({ name: 'stopDebugging' }),
                () => waitForNoDebugSessions().catch(() => undefined),
                () => waitForNoRunningAppHost(90000, appHostSourcePath).catch(() => undefined),
                () => waitForRepositoryIdle(),
            ], 'Java debug startup timeout E2E cleanup failed.');
        }
    });
});

function shouldRunStartupTimeoutProof(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_UNSET_CLI_START_TIMEOUT === 'true';
}

function getMarkerTimestamp(output: string, marker: string): Date {
    const match = new RegExp(`${marker} ([^\\s]+)`).exec(output);
    assert.ok(match, `Expected debug console output to contain a timestamp after '${marker}': ${output}`);

    const timestamp = new Date(match[1].replace(/(\.\d{3})\d+Z$/, '$1Z'));
    assert.ok(!Number.isNaN(timestamp.getTime()), `Expected '${match[1]}' to be a valid timestamp.`);
    return timestamp;
}
