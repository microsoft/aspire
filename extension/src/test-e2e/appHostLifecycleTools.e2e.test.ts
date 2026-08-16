import * as assert from 'assert';
import { spawn, type ChildProcessWithoutNullStreams } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { findRunningAppHost, getCommandInvocationCount, getDebugLaunchCount, isSamePath, readStateFile, waitForCommandOutcome, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceAppHostConfig, runE2eTeardown, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { runProcess, terminateProcessTree } from './helpers/process';
import { getProcessEntry, listProcessEntries, type ProcessEntry } from './helpers/processArguments';
import { ensureDiagnosticsDir, getCliPath, getPrimaryAppHostProjectPath, getRepoRoot, getRunRoot, getWorkspaceRoot } from './helpers/paths';
import { acceptModalDialog, openAspireView, type AcceptedModalDialog } from './helpers/vscode';
import { assertLinkedAppHostCliLaunch, commandLineArgumentEquals } from '../utils/processArguments';

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
    invocationMessage?: string;
    confirmationTitle?: string;
    confirmationMessage?: string;
}

interface RegisteredTool {
    name: string;
    tags: string[];
    description: string;
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
            assert.deepStrictEqual(stopInvocation.results, [{
                tool: stopToolName,
                outcome: 'stopped',
                appHostPath: relativeAppHostPath,
                controller: 'external',
            }]);

            await waitForNoRunningAppHost(180000, appHostPath);
            await waitForChildProcessExit(externalRun, 180000);
            await waitForProcessExit(externalAppHostPid, 180000);
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

    test('starts a linked-worktree AppHost with inferred isolation through vscode.lm.invokeTool', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const fixture = await createLinkedWorktreeAppHostFixture();
        let artifact: Record<string, unknown> = {};

        try {
            const relativeAppHostPath = path.relative(getWorkspaceRoot(), fixture.appHostPath).split(path.sep).join('/');
            artifact = {
                status: 'created',
                ...fixture,
                relativeAppHostPath,
            };
            Object.assign(artifact, {
                cli: {
                    path: getCliPath(),
                    version: (await runProcess(getCliPath(), ['--version'], { timeoutMs: 60000 })).stdout.trim(),
                    repositoryHead: (await runProcess('git', ['rev-parse', 'HEAD'], { cwd: getRepoRoot(), timeoutMs: 30000 })).stdout.trim(),
                },
            });
            writeLinkedWorktreeArtifact(artifact);

            writeWorkspaceAppHostConfigForPath(fixture.appHostPath);
            const refreshBefore = getCommandInvocationCount('aspire-vscode.refreshAppHosts');
            await executeE2eControlCommand({ name: 'refreshAppHosts' });
            await waitForCommandOutcome('aspire-vscode.refreshAppHosts', 'success', 60000, refreshBefore);
            const discovered = await waitForSelectedWorkspaceAppHost(fixture.appHostPath);
            assert.strictEqual(discovered.state.workspaceAppHostPath, fixture.appHostPath);

            const preparedStart = await invokeControlCommand<PreparedInvocation>({
                name: 'prepareLanguageModelToolInvocation',
                toolName: startToolName,
                input: { appHostPath: relativeAppHostPath, mode: 'debug' },
            });
            assert.strictEqual(preparedStart.confirmationTitle, 'Start Aspire AppHost');
            assert.strictEqual(preparedStart.confirmationMessage, `Start the Aspire AppHost ${relativeAppHostPath} in debug mode with isolation?`);

            const startInvocation = await invokeLifecycleTool({
                name: 'invokeLanguageModelTool',
                toolName: startToolName,
                input: { appHostPath: relativeAppHostPath, mode: 'debug' },
            }, 600000, 1, 'apphost-lifecycle-linked-worktree-start-confirmation');
            assert.strictEqual(startInvocation.dialogs[0].message, 'Start Aspire AppHost');
            assert.strictEqual(startInvocation.dialogs[0].details, `Start the Aspire AppHost ${relativeAppHostPath} in debug mode with isolation?`);
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
            assert.strictEqual(processInfo.appHostPath, fixture.appHostPath);
            const cliPid = processInfo.cliPid;
            assert.ok(cliPid, `Expected the E2E state bridge to report the linked AppHost CLI process: ${JSON.stringify(processInfoStatus)}`);

            const cliProcess = await waitForLinkedAppHostCliProcess(cliPid, fixture.appHostPath, 180000);
            const extensionLog = await waitForLinkedAppHostSpawnLog(fixture.appHostPath, 60000);

            const runningState = readStateFile();
            assert.strictEqual(runningState.state.workspaceAppHostPath, fixture.appHostPath);
            const activeDebugSession = runningState.state.debugSessions.find(session =>
                session.appHostPath === fixture.appHostPath && session.startupCompleted);
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
            writeLinkedWorktreeArtifact(artifact);

            const stopInvocation = await invokeLifecycleTool({
                name: 'invokeLanguageModelTool',
                toolName: stopToolName,
                input: { appHostPath: relativeAppHostPath },
            }, 300000, 1, 'apphost-lifecycle-linked-worktree-stop-confirmation');
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
            writeLinkedWorktreeArtifact(artifact);
        }
        catch (error) {
            Object.assign(artifact, {
                status: 'failed',
                error: error instanceof Error ? `${error.name}: ${error.message.split(/\r?\n/, 1)[0]}` : String(error),
            });
            writeLinkedWorktreeArtifact(artifact);
            throw error;
        }
        finally {
            await cleanupLinkedWorktreeAppHostFixture(fixture);
        }
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
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
`);
        fs.writeFileSync(path.join(projectDirectory, 'AppHost.cs'), `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

async function cleanupLinkedWorktreeAppHostFixture(fixture: LinkedWorktreeAppHostFixture): Promise<void> {
    await runE2eTeardown([
        () => executeE2eControlCommand({ name: 'stopDebugging' }),
        () => stopAppHostIfRunning(fixture.appHostPath),
        () => waitForNoDebugSessions(180000),
        () => waitForNoRunningAppHost(180000, fixture.appHostPath),
        () => restorePrimaryWorkspaceAppHostSelection(),
        () => removeLinkedWorktreePaths(fixture.seedRepositoryPath, fixture.linkedWorktreePath),
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

async function waitForLinkedAppHostCliProcess(cliPid: number, appHostPath: string, timeoutMs: number): Promise<ProcessEntry> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const cliProcess = await getProcessEntry(cliPid);
        if (cliProcess) {
            assertLinkedAppHostCliLaunch(cliProcess.arguments, appHostPath, getCliPath());
            return cliProcess;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for Aspire CLI process ${cliPid} to launch ${appHostPath}.`);
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
                candidate.includes(`--apphost ${appHostPath}; cwd=`));
            if (line) {
                return { path: logPath, line };
            }
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for Aspire Extension.log to record the linked AppHost launch for ${appHostPath}.`);
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

function writeLinkedWorktreeArtifact(artifact: Record<string, unknown>): void {
    const artifactPath = path.join(ensureDiagnosticsDir(), 'apphost-lifecycle-linked-worktree.json');
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
    try {
        process.kill(pid, 0);
        return true;
    }
    catch (error) {
        return !(error && typeof error === 'object' && 'code' in error && error.code === 'ESRCH');
    }
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
    const artifactPath = path.join(ensureDiagnosticsDir(), 'apphost-lifecycle-language-model-tools.json');
    fs.writeFileSync(artifactPath, JSON.stringify(artifact, undefined, 2));
}
