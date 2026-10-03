import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { ProjectLaunchConfiguration } from '../dcp/types';
import { commandLineArgumentEquals } from '../test/helpers/processArguments';
import { getCommandInvocationCount, waitForCommandOutcome, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, removePath, restoreWorkspaceAppHostConfig, runE2eTeardown, waitForKnownProcessExit, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { runProcess } from './helpers/process';
import { getProcessEntry, listProcessEntries } from './helpers/processArguments';
import { getCliPath, getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { acceptModalDialog, openAspireView } from './helpers/vscode';

suite('Aspire launch profiles E2E', function () {
    this.timeout(240000);

    const appHostProjectPath = getPrimaryAppHostProjectPath();
    const appHostDirectory = path.dirname(appHostProjectPath);
    const launchSettingsDirectory = path.join(appHostDirectory, 'Properties');
    const launchSettingsPath = path.join(launchSettingsDirectory, 'launchSettings.json');
    const launchJsonPath = path.join(getWorkspaceRoot(), '.vscode', 'launch.json');
    const denoAppHostDirectory = path.join(getWorkspaceRoot(), 'DenoAppHost');
    const denoAppHostPath = path.join(denoAppHostDirectory, 'apphost.mts');
    let originalLaunchSettings: FileSnapshot | undefined;
    let originalLaunchJson: FileSnapshot | undefined;
    let launchSettingsDirectoryExisted: boolean | undefined;
    let debugAppHostPath: string | undefined;

    setup(() => {
        originalLaunchSettings = undefined;
        originalLaunchJson = undefined;
        launchSettingsDirectoryExisted = undefined;
        debugAppHostPath = undefined;
    });

    teardown(async () => {
        const denoDebugRequested = debugAppHostPath === denoAppHostPath;
        let denoProcessIds: number[] | undefined;
        let debuggingStopped = false;
        let denoProcessesExited = false;

        await runE2eTeardown([
            async () => {
                if (denoDebugRequested) {
                    // A Deno process waiting for its inspector is not yet in the AppHost registry.
                    // Match the fixture's exact argv path before stopping its owning debugger.
                    // VS Code can use c:\... while Deno uses C:\... on Windows. The listing hint
                    // is case-sensitive, so compare complete path arguments after listing there.
                    const processes = await listProcessEntries(process.platform === 'win32' ? undefined : denoAppHostPath);
                    denoProcessIds = processes
                        .filter(entry => entry.arguments.some(argument => commandLineArgumentEquals(argument, denoAppHostPath)))
                        .map(entry => entry.pid);
                }
            },
            async () => {
                if (debugAppHostPath) {
                    // Leave time in the hook for process discovery (60s), exit (30s), and restoration.
                    // The bridge already waits for debug sessions and launching/stopping state to clear.
                    await executeE2eControlCommand({ name: 'stopDebugging' }, { timeoutMs: 120000 });
                    debuggingStopped = true;
                }
            },
            async () => {
                if (denoProcessIds) {
                    await Promise.all(denoProcessIds.map(pid =>
                        waitForKnownProcessExit(pid, 'a Deno AppHost fixture process', 30000)));
                    denoProcessesExited = true;
                }
            },
            () => restoreWorkspaceAppHostConfig(),
            () => {
                // Preserve a failed shutdown's fixture rather than deleting files under live processes.
                // runE2eTeardown still reports the stop/exit failure and restores the independent files.
                if (!denoDebugRequested || (debuggingStopped && denoProcessesExited)) {
                    removePath(denoAppHostDirectory, { recursive: true, force: true });
                }
            },
            () => restoreFile(launchSettingsPath, originalLaunchSettings),
            () => restoreFile(launchJsonPath, originalLaunchJson),
            () => removeDirectoryIfCreated(launchSettingsDirectory, launchSettingsDirectoryExisted),
        ], 'Launch profiles E2E teardown failed.');
    });

    test('debugs a Deno AppHost through the built-in JavaScript debugger', async function () {
        this.timeout(600000);

        fs.mkdirSync(denoAppHostDirectory, { recursive: true });
        // Select Deno before init installs dependencies. A package.json marker would instead
        // make scaffolding treat this as a brownfield project and nest the AppHost in a subdirectory.
        writeFileWithRetry(path.join(denoAppHostDirectory, 'deno.json'), '{}\n');
        await runProcess(
            getCliPath(),
            ['init', '--language', 'typescript', '--non-interactive', '--suppress-agent-init'],
            { cwd: denoAppHostDirectory, timeoutMs: 180000 });
        writeWorkspaceAppHostConfigForPath(denoAppHostPath);
        await openAspireView();
        await waitForSelectedWorkspaceAppHost(denoAppHostPath, 180000);

        const beforeDebug = getCommandInvocationCount('aspire-vscode.debugAppHost');
        debugAppHostPath = denoAppHostPath;
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath: denoAppHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, beforeDebug);
        // Deno's --inspect-wait blocks before evaluating apphost.mts, so AppHost startup proves
        // that pwa-node connected to the inspector and allowed execution to continue.
        await waitForDebugSessionStartup(denoAppHostPath, 300000);
    });

    test('uses the AppHost launch profile selected by launch.json', async () => {
        originalLaunchSettings = captureFile(launchSettingsPath);
        launchSettingsDirectoryExisted = fs.existsSync(launchSettingsDirectory);

        await openAspireView();
        await waitForRepositoryIdle();

        fs.mkdirSync(launchSettingsDirectory, { recursive: true });
        writeFileWithRetry(launchSettingsPath, JSON.stringify({
            profiles: {
                h1: {
                    commandName: 'Project',
                    environmentVariables: {
                        mode: '1',
                    },
                },
                h2: {
                    commandName: 'Project',
                    applicationUrl: 'http://localhost:15002',
                    environmentVariables: {
                        mode: '2',
                    },
                },
            },
        }, undefined, 2));

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: appHostProjectPath,
        };
        const controlStatus = await executeE2eControlCommand({
            name: 'createResourceDebugConfiguration',
            launchConfig,
            env: [
                { name: 'mode', value: '1' },
                { name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' },
                { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' },
                { name: 'EXPLICIT', value: 'from-cli' },
            ],
            debug: false,
            isApphost: true,
            debuggers: {
                apphost: {
                    launchProfile: 'h2',
                    env: {
                        EXPLICIT: 'from-launch-json',
                    },
                },
            },
            environmentKeys: ['mode', 'DOTNET_LAUNCH_PROFILE', 'ASPNETCORE_URLS', 'EXPLICIT'],
        }, { timeoutMs: 180000 });
        const debugConfiguration = controlStatus.result as PreparedDebugConfiguration;

        assert.deepStrictEqual(debugConfiguration.environment, {
            mode: '2',
            DOTNET_LAUNCH_PROFILE: 'h2',
            ASPNETCORE_URLS: 'http://localhost:15002',
            EXPLICIT: 'from-launch-json',
        });

        const cliHandoffLaunchConfig: ProjectLaunchConfiguration = {
            ...launchConfig,
            launch_profile: 'H2',
        };
        const cliHandoffStatus = await executeE2eControlCommand({
            name: 'createResourceDebugConfiguration',
            launchConfig: cliHandoffLaunchConfig,
            env: [
                { name: 'mode', value: '1' },
                { name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' },
                { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' },
            ],
            debug: false,
            isApphost: true,
            environmentKeys: ['mode', 'DOTNET_LAUNCH_PROFILE', 'ASPNETCORE_URLS'],
        }, { timeoutMs: 180000 });
        const cliHandoffConfiguration = cliHandoffStatus.result as PreparedDebugConfiguration;

        assert.deepStrictEqual(cliHandoffConfiguration.environment, {
            mode: '2',
            DOTNET_LAUNCH_PROFILE: 'H2',
            ASPNETCORE_URLS: 'http://localhost:15002',
        });
    });

    test('forwards launch profiles from launch.json and the AppHost start tool to the selected CLI', async () => {
        originalLaunchSettings = captureFile(launchSettingsPath);
        originalLaunchJson = captureFile(launchJsonPath);
        launchSettingsDirectoryExisted = fs.existsSync(launchSettingsDirectory);

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? appHostProjectPath;
        const relativeAppHostPath = path.relative(getWorkspaceRoot(), appHostPath).split(path.sep).join('/');

        fs.mkdirSync(launchSettingsDirectory, { recursive: true });
        writeFileWithRetry(launchSettingsPath, JSON.stringify({
            profiles: {
                'Development HTTPS': {
                    commandName: 'Project',
                },
                '--no-build': {
                    commandName: 'Project',
                },
            },
        }, undefined, 2));

        const launchConfigurationName = 'Aspire launch profile E2E';
        fs.mkdirSync(path.dirname(launchJsonPath), { recursive: true });
        writeFileWithRetry(launchJsonPath, JSON.stringify({
            version: '0.2.0',
            configurations: [{
                type: 'aspire',
                request: 'launch',
                name: launchConfigurationName,
                program: appHostPath,
                command: 'run',
                launchProfile: 'Development HTTPS',
                dashboardBrowser: 'none',
            }],
        }, undefined, 2));

        debugAppHostPath = appHostPath;
        const launchStatus = await executeE2eControlCommand({
            name: 'startDebugging',
            configurationName: launchConfigurationName,
        }, { timeoutMs: 600000 });
        assert.strictEqual(launchStatus.result, true);
        await waitForDebugSessionStartup(appHostPath, 600000);
        assert.deepStrictEqual(
            await getAspireCliLaunchProfileArgument(appHostPath),
            '--launch-profile=Development HTTPS');

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions(180000);
        await waitForNoRunningAppHost(180000, appHostPath);

        const invocation = invokeLanguageModelTool({
            appHostPath: relativeAppHostPath,
            mode: 'run',
            launchProfile: '--no-build',
        });
        invocation.catch(() => undefined);
        const dialog = await acceptModalDialog('Yes', 180000, 'launch-profile-language-model-tool');
        const toolResult = await invocation;

        assert.strictEqual(dialog.message, 'Start Aspire AppHost');
        assert.strictEqual(dialog.details, `Start the Aspire AppHost ${relativeAppHostPath} in run mode using launch profile --no-build?`);
        assert.strictEqual(toolResult.outcome, 'started');
        assert.strictEqual(toolResult.requestedMode, 'run');
        assert.strictEqual(toolResult.effectiveMode, 'run');
        await waitForDebugSessionStartup(appHostPath, 600000);
        assert.deepStrictEqual(
            await getAspireCliLaunchProfileArgument(appHostPath),
            '--launch-profile=--no-build');
    });
});

interface PreparedDebugConfiguration {
    environment: Record<string, string | undefined>;
}

interface LifecycleToolResult {
    outcome: string;
    requestedMode?: string;
    effectiveMode?: string;
}

type FileSnapshot =
    | { exists: false }
    | { exists: true; content: string };

async function invokeLanguageModelTool(input: Record<string, unknown>): Promise<LifecycleToolResult> {
    const status = await executeE2eControlCommand({
        name: 'invokeLanguageModelTool',
        toolName: 'aspire_apphost_start',
        input,
    }, { timeoutMs: 600000 });
    if (status.errorMessage) {
        throw new Error(`AppHost start tool failed: ${status.errorMessage}`);
    }

    const result = status.result as { results: string[] };
    assert.strictEqual(result.results.length, 1);
    return JSON.parse(result.results[0]) as LifecycleToolResult;
}

async function getAspireCliLaunchProfileArgument(appHostPath: string): Promise<string | undefined> {
    const status = await executeE2eControlCommand({ name: 'getDebugSessionProcessInfo', appHostPath });
    const processInfo = status.result as { cliPid?: number };
    assert.ok(processInfo.cliPid, `Expected the E2E state bridge to report the Aspire CLI process: ${JSON.stringify(status)}`);

    const processEntry = await getProcessEntry(processInfo.cliPid);
    assert.ok(processEntry, `Expected Aspire CLI process ${processInfo.cliPid} to still be running.`);
    return processEntry.arguments.find(argument => argument.startsWith('--launch-profile='));
}

function captureFile(filePath: string): FileSnapshot {
    return fs.existsSync(filePath)
        ? { exists: true, content: fs.readFileSync(filePath, 'utf8') }
        : { exists: false };
}

function restoreFile(filePath: string, snapshot: FileSnapshot | undefined): void {
    if (snapshot === undefined) {
        return;
    }

    if (!snapshot.exists) {
        fs.rmSync(filePath, { force: true });
        return;
    }

    writeFileWithRetry(filePath, snapshot.content);
}

function removeDirectoryIfCreated(directoryPath: string, existed: boolean | undefined): void {
    if (existed === false && fs.existsSync(directoryPath)) {
        fs.rmdirSync(directoryPath);
    }
}
