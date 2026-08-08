import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getDebugLaunchCount, isSamePath, readStateFile, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { runProcess } from './helpers/process';
import { ensureDiagnosticsDir, getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { acceptModalDialog, openAspireView, type AcceptedModalDialog } from './helpers/vscode';

interface LifecycleToolResult {
    tool: string;
    outcome: string;
    appHostPath: string;
    requestedMode?: string;
    effectiveMode?: string;
    controller: string;
}

interface PreparedInvocation {
    invocationMessage?: string;
    confirmationTitle?: string;
    confirmationMessage?: string;
}

interface RegisteredTool {
    name: string;
    tags: string[];
    description: string;
}

const startToolName = 'aspire_apphost_start';
const stopToolName = 'aspire_apphost_stop';

suite('Aspire AppHost lifecycle language model tools E2E', function () {
    this.timeout(900000);

    teardown(async () => {
        await runE2eTeardown([
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
        assert.deepStrictEqual(registeredTools.map(tool => tool.name), [startToolName, stopToolName]);

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

        assert.strictEqual(preparedStart.confirmationTitle, 'Start Aspire AppHost');
        assert.strictEqual(preparedStart.confirmationMessage, `Start the Aspire AppHost ${relativeAppHostPath} in debug mode?`);
        assert.strictEqual(preparedStop.confirmationTitle, 'Stop Aspire AppHost');
        assert.strictEqual(preparedStop.confirmationMessage, `Stop the Aspire AppHost ${relativeAppHostPath}?`);

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
        await waitForNoRunningAppHost(180000, appHostPath);
        assert.strictEqual(readStateFile().state.debugSessions.length, 0, 'Expected no debug sessions after the stop tool call.');
        assert.deepStrictEqual(await waitForAppHostProcessCount(appHostPath, 0, 180000), [], 'Expected no AppHost processes after the stop tool call.');

        const stopAgainResults = (await invokeLifecycleTool({
            name: 'invokeLanguageModelTool',
            toolName: stopToolName,
            input: { appHostPath: relativeAppHostPath },
        }, 120000, 1)).results;
        assert.strictEqual(stopAgainResults[0].outcome, 'notRunning');
        assert.strictEqual(stopAgainResults[0].controller, 'none');

        writeLifecycleToolArtifact({
            relativeAppHostPath,
            appHostPid,
            registeredTools,
            preparedStart,
            preparedStop,
            confirmationDialogs: [
                ...concurrentStartInvocation.dialogs,
                repeatedStartInvocation.dialogs[0],
                stopInvocation.dialogs[0],
            ],
            concurrentStarts,
            repeatedStart: repeatedStart[0],
            stop: stopResults[0],
            stopAgain: stopAgainResults[0],
        });
    });
});

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
): Promise<{ results: LifecycleToolResult[]; dialogs: AcceptedModalDialog[] }> {
    const invocation = invokeControlCommand<{ results: string[] }>(command, timeoutMs);
    // Keep the rejection observed while the dialogs are being answered; the real failure
    // is reported when the invocation is awaited below.
    invocation.catch(() => undefined);

    const dialogs: AcceptedModalDialog[] = [];
    for (let index = 0; index < expectedConfirmations; index++) {
        dialogs.push(await acceptModalDialog('Yes', 180000, index === 0 ? screenshotName : undefined));
    }

    const result = await invocation;
    return { results: result.results.map(item => JSON.parse(item) as LifecycleToolResult), dialogs };
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
    const processes = process.platform === 'win32'
        ? await listWindowsProcesses()
        : await listPosixProcesses();

    return processes
        .filter(entry => entry.commandLine.includes('--start-debug-session') && entry.commandLine.includes(appHostPath))
        .map(entry => entry.pid)
        .sort((left, right) => left - right);
}

async function listPosixProcesses(): Promise<{ pid: number; commandLine: string }[]> {
    // `ps -A -w -w -o pid=,args=` prints one process per line with no header, for example:
    //   " 51234 /path/to/aspire run --start-debug-session --nologo --apphost /path/App.csproj"
    // The repeated `-w` disables the default command-line truncation, which would otherwise
    // cut off the `--apphost` argument this match depends on.
    const result = await runProcess('/bin/ps', ['-A', '-w', '-w', '-o', 'pid=,args='], { timeoutMs: 30000 });
    return result.stdout
        .split('\n')
        .map(line => line.trim())
        .filter(line => line.length > 0)
        .map(line => {
            const separatorIndex = line.indexOf(' ');
            return { pid: Number.parseInt(line.slice(0, separatorIndex), 10), commandLine: line.slice(separatorIndex + 1) };
        })
        .filter(entry => Number.isInteger(entry.pid));
}

async function listWindowsProcesses(): Promise<{ pid: number; commandLine: string }[]> {
    const result = await runProcess('powershell.exe', [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        'Get-CimInstance Win32_Process | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress',
    ], { timeoutMs: 60000 });

    const parsed = JSON.parse(result.stdout) as { ProcessId: number; CommandLine: string | null }[] | { ProcessId: number; CommandLine: string | null };
    const entries = Array.isArray(parsed) ? parsed : [parsed];
    return entries.map(entry => ({ pid: entry.ProcessId, commandLine: entry.CommandLine ?? '' }));
}

function writeLifecycleToolArtifact(artifact: Record<string, unknown>): void {
    const artifactPath = path.join(ensureDiagnosticsDir(), 'apphost-lifecycle-language-model-tools.json');
    fs.writeFileSync(artifactPath, JSON.stringify(artifact, undefined, 2));
}
