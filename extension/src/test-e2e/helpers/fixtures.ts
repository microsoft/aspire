import * as fs from 'fs';
import * as path from 'path';
import type { AspireExtensionE2EControlCommand, AspireExtensionE2EControlStatus } from '../../types/extensionApi';
import { applyE2eControl, isSamePath, readStateFile, sleepSynchronously, waitForExtensionState } from './assertions';
import { getCliPath, getPrimaryAppHostProjectPath, getRepoRoot, getWorkspaceRoot } from './paths';
import { runProcess } from './process';

const csharpFileHeader = `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

`;

export function getWorkspaceSettingsPath(): string {
    return path.join(getWorkspaceRoot(), '.vscode', 'settings.json');
}

export function getGeneratedProjectRoot(projectName: string): string {
    const workspaceRoot = path.resolve(getWorkspaceRoot());
    const projectRoot = path.resolve(workspaceRoot, projectName);
    const relativePath = path.relative(workspaceRoot, projectRoot);
    if (relativePath === '' || relativePath.startsWith('..') || path.isAbsolute(relativePath)) {
        throw new Error(`Generated E2E project path must stay under the workspace root. Project name: ${projectName}`);
    }

    return projectRoot;
}

export function getGeneratedAppHostPath(projectName: string): string {
    return path.join(getGeneratedProjectRoot(projectName), 'apphost.cs');
}

export async function writeWorkspaceCliPath(cliPath: string): Promise<void> {
    const settingsPath = getWorkspaceSettingsPath();
    const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8')) as Record<string, unknown>;
    settings['aspire.aspireCliExecutablePath'] = cliPath;
    writeFileWithRetry(settingsPath, JSON.stringify(settings, undefined, 2));

    await applyE2eControl({ aspireCliExecutablePath: cliPath });
}

export async function setE2eCliPathForE2E(cliPath: string | undefined): Promise<void> {
    await applyE2eControl({ e2eCliExecutablePath: cliPath ?? null });
}

export async function restoreE2eCliPathForE2E(): Promise<void> {
    await setE2eCliPathForE2E(getCliPath());
}

export async function setCliUnavailableForE2E(forceCliUnavailable: boolean): Promise<void> {
    await applyE2eControl({ forceCliUnavailable });
}

export async function setTerminalCommandExecutionSuppressedForE2E(suppressTerminalCommandExecution: boolean): Promise<void> {
    await applyE2eControl({ suppressTerminalCommandExecution });
}

export async function setDebugLaunchSuppressedForE2E(suppressDebugLaunch: boolean): Promise<void> {
    await applyE2eControl({ suppressDebugLaunch });
}

export async function setShowStatusDelayForE2E(delayMs: number | undefined): Promise<void> {
    await applyE2eControl({ showStatusDelayMs: delayMs ?? null });
}

export async function executeE2eControlCommand(
    command: AspireExtensionE2EControlCommand,
    options?: { waitFor?: 'started' | 'applied'; timeoutMs?: number }
): Promise<AspireExtensionE2EControlStatus> {
    return await applyE2eControl({ command }, options?.waitFor ?? 'applied', options?.timeoutMs);
}

export async function runE2eTeardown(cleanups: ReadonlyArray<() => unknown | Promise<unknown>>, failureMessage: string): Promise<void> {
    const failures: unknown[] = [];
    for (const cleanup of cleanups) {
        try {
            await cleanup();
        } catch (error) {
            failures.push(error);
        }
    }

    if (failures.length > 0) {
        throw new AggregateError(failures.map(redactE2eTeardownFailure), formatE2eTeardownFailureMessage(failureMessage, failures.map(redactE2eTeardownFailure)));
    }
}

function formatE2eTeardownFailureMessage(failureMessage: string, failures: ReadonlyArray<string>): string {
    const formattedFailures = failures.map((failure, index) => `${index + 1}. ${failure}`);

    return `${failureMessage}\n${formattedFailures.join('\n')}`;
}

function redactE2eTeardownFailure(failure: unknown): string {
    const details = failure instanceof Error ? `${failure.name}: ${failure.message}` : String(failure);

    return details
        .replace(/https?:\/\/\S+/g, '<redacted-url>')
        .replace(/Last state:[\s\S]*/g, 'Last state: <redacted>');
}

export async function createEmptyAppHostProject(projectName: string): Promise<string> {
    const outputPath = getGeneratedProjectRoot(projectName);
    removePath(outputPath, { recursive: true, force: true });
    await runProcess(getCliPath(), [
        'new',
        'aspire-empty',
        '--name',
        projectName,
        '--output',
        outputPath,
        '--language',
        'csharp',
        ...getPackageSourceArgs(),
        '--suppress-agent-init',
        '--non-interactive',
        '--nologo',
    ], {
        cwd: getWorkspaceRoot(),
        timeoutMs: 180000,
    });
    await waitForPath(getGeneratedAppHostPath(projectName), 180000);
    await waitForPath(path.join(outputPath, 'aspire.config.json'), 180000);

    return outputPath;
}

export async function addIntegrationPackageToAppHost(integration: string, appHostPath: string): Promise<void> {
    await runProcess(getCliPath(), [
        'add',
        integration,
        '--apphost',
        appHostPath,
        '--non-interactive',
        '--nologo',
    ], {
        cwd: getWorkspaceRoot(),
        timeoutMs: 180000,
    });
    await waitForFileContent(appHostPath, integration, 180000);
}

export async function setSourceBreakpoint(filePath: string, line: number): Promise<void> {
    await executeE2eControlCommand({ name: 'setSourceBreakpoint', filePath, line, clearExisting: true });
    await waitForExtensionState(
        file => Array.isArray(file.control?.result) &&
            file.control.result.some((breakpoint: unknown) => isBreakpointAt(breakpoint, filePath, line)),
        `source breakpoint in ${filePath}:${line + 1}`,
        10000);
}

export async function clearBreakpoints(): Promise<void> {
    await executeE2eControlCommand({ name: 'clearBreakpoints' });
}

export async function removeGeneratedProject(projectName: string, knownAppHostPid?: number): Promise<void> {
    await waitForNoRunningAppHostPathOrStopKnownProcess(getGeneratedAppHostPath(projectName), 30000, knownAppHostPid, 'before deleting');
    removePath(getGeneratedProjectRoot(projectName), { recursive: true, force: true });
}

export function getRunningAppHostPid(appHostPath: string): number | undefined {
    return getRunningAppHostFromState(appHostPath)?.appHostPid;
}

export async function waitForRunningAppHostPid(appHostPath: string, timeoutMs: number): Promise<number> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const pid = getRunningAppHostPid(appHostPath);
        if (pid !== undefined) {
            return pid;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for AppHost process state before stopping ${appHostPath}.`);
}

export function removePrimaryAppHostFixture(): void {
    removePath(path.join(getWorkspaceRoot(), 'AspireE2E.AppHost'), { recursive: true, force: true });
    removePath(path.join(getWorkspaceRoot(), 'AspireE2E.Worker'), { recursive: true, force: true });
    removeWorkspaceAppHostConfig();
}

export function writeNoCapabilitiesCliWrapper(name = 'aspire-no-capabilities'): string {
    return writeCliWrapper(name, {
        configInfoJson: {
            localSettingsPath: path.join(getWorkspaceRoot(), 'aspire.config.json'),
            globalSettingsPath: path.join(getWorkspaceRoot(), 'global-aspire.config.json'),
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [],
        },
    });
}

export function writeConfigInfoUnsupportedCliWrapper(name = 'aspire-no-config-info'): string {
    return writeCliWrapper(name, {
        configInfoExitCode: 42,
        configInfoStderr: 'config info is not available in this simulated old CLI',
    });
}

export async function restoreWorkspaceCliPath(): Promise<void> {
    await writeWorkspaceCliPath(getCliPath());
}

export function removeWorkspaceAppHostConfig(): void {
    removePath(getWorkspaceAppHostConfigPath(), { force: true });
}

export function writeWorkspaceAppHostConfig(value: unknown): void {
    writeFileWithRetry(getWorkspaceAppHostConfigPath(), JSON.stringify(value, undefined, 2));
}

export function writeWorkspaceAppHostConfigRaw(value: string): void {
    writeFileWithRetry(getWorkspaceAppHostConfigPath(), value);
}

export function restoreWorkspaceAppHostConfig(): void {
    writeWorkspaceAppHostConfig({
        appHost: {
            path: path.join('AspireE2E.AppHost', 'AspireE2E.AppHost.csproj'),
        },
    });
}

export function writeWorkspaceAppHostConfigForPath(appHostPath: string): void {
    const relativePath = path.relative(getWorkspaceRoot(), appHostPath);
    writeWorkspaceAppHostConfig({
        appHost: {
            path: relativePath,
        },
    });
}

export function writeWorkspaceSetting(key: string, value: unknown): void {
    const settingsPath = getWorkspaceSettingsPath();
    const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8')) as Record<string, unknown>;
    settings[key] = value;
    writeFileWithRetry(settingsPath, JSON.stringify(settings, undefined, 2));
}

export function writeLegacyAspireSettings(appHostPath = path.join('..', 'AspireE2E.AppHost', 'AspireE2E.AppHost.csproj')): void {
    const settingsPath = getLegacyAspireSettingsPath();
    fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
    writeFileWithRetry(settingsPath, JSON.stringify({ appHostPath }, undefined, 2));
}

export function removeLegacyAspireSettings(): void {
    removePath(path.join(getWorkspaceRoot(), '.aspire'), { recursive: true, force: true });
}

export function createAdditionalAppHostCandidate(projectName = 'AspireE2E.SecondAppHost', kind: 'project' | 'single-file' = 'project'): string {
    const projectDirectory = path.join(getWorkspaceRoot(), projectName);
    fs.mkdirSync(projectDirectory, { recursive: true });

    if (kind === 'single-file') {
        const appHostPath = path.join(projectDirectory, 'apphost.cs');
        fs.writeFileSync(appHostPath, `${csharpFileHeader}#:sdk Aspire.AppHost.Sdk@${getAppHostSdkVersion()}

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`);

        return appHostPath;
    }

    fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Aspire.AppHost.Sdk/${getAppHostSdkVersion()}">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
`);

    fs.writeFileSync(path.join(projectDirectory, 'AppHost.cs'), `${csharpFileHeader}var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`);

    return path.join(projectDirectory, `${projectName}.csproj`);
}

export function removeAdditionalAppHostCandidate(projectName = 'AspireE2E.SecondAppHost'): void {
    removePath(path.join(getWorkspaceRoot(), projectName), { recursive: true, force: true });
}

export async function stopPrimaryAppHostIfRunning(): Promise<void> {
    await stopAppHostIfRunning(getPrimaryAppHostProjectPath());
}

export async function stopAppHostIfRunning(appHostPath: string): Promise<void> {
    const runningAppHostBeforeStop = getRunningAppHostFromState(appHostPath);
    const stopError = await tryStopAppHost(appHostPath);

    if (!stopError) {
        await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');
        return;
    }

    if (/not running|No running AppHost|No AppHost/i.test(stopError.message)) {
        await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');
        return;
    }

    if (/timed out|Failed to stop/i.test(stopError.message)) {
        const runningAppHost = await getRunningAppHostAccordingToCli(appHostPath);
        if (!runningAppHost) {
            await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');
            return;
        }

        try {
            await waitForProcessExit(runningAppHost.appHostPid, 30000);
        }
        catch {
            if (isProcessRunning(runningAppHost.appHostPid)) {
                await stopProcess(runningAppHost.appHostPid, 30000);
            }
        }

        if (!await getRunningAppHostAccordingToCli(appHostPath)) {
            await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');
            return;
        }

        if (isProcessRunning(runningAppHost.appHostPid)) {
            await stopProcess(runningAppHost.appHostPid, 30000);
            await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');
            return;
        }

        throw new Error(`AppHost is still running according to aspire ps: ${appHostPath}`);
    }

    throw stopError;
}

async function tryStopAppHost(appHostPath: string): Promise<Error | undefined> {
    try {
        await runProcess(getCliPath(), ['stop', '--non-interactive', '--apphost', appHostPath], {
            cwd: getWorkspaceRoot(),
            timeoutMs: 60000,
        });
        return undefined;
    } catch (error) {
        if (error instanceof Error) {
            return error;
        }

        throw error;
    }
}

interface PsAppHost {
    appHostPath: string;
    appHostPid: number;
    status?: string;
}

async function getRunningAppHostAccordingToCli(appHostPath: string): Promise<PsAppHost | undefined> {
    const result = await runProcess(getCliPath(), ['ps', '--format', 'json'], {
        cwd: getWorkspaceRoot(),
        timeoutMs: 30000,
    });
    const appHosts = JSON.parse(result.stdout) as unknown;

    if (!Array.isArray(appHosts)) {
        throw new Error(`Unexpected aspire ps JSON output: ${result.stdout}`);
    }

    return appHosts.find(candidate => {
        if (!isPsAppHost(candidate)) {
            return false;
        }

        return candidate.status !== 'stopped' && isSamePath(candidate.appHostPath, appHostPath);
    });
}

function isPsAppHost(value: unknown): value is PsAppHost {
    if (typeof value !== 'object' || value === null) {
        return false;
    }

    const candidate = value as { appHostPath?: unknown; appHostPid?: unknown; status?: unknown };
    return typeof candidate.appHostPath === 'string'
        && typeof candidate.appHostPid === 'number'
        && Number.isInteger(candidate.appHostPid)
        && candidate.appHostPid > 0
        && (candidate.status === undefined || typeof candidate.status === 'string');
}

async function waitForNoRunningAppHostPath(appHostPath: string, timeoutMs: number, knownAppHostPid: number | undefined, actionDescription: string): Promise<void> {
    const started = Date.now();
    let lastKnownAppHostPid = knownAppHostPid;

    while (Date.now() - started < timeoutMs) {
        const runningAppHost = getRunningAppHostFromState(appHostPath);
        if (runningAppHost) {
            lastKnownAppHostPid = runningAppHost.appHostPid;
        }

        if (lastKnownAppHostPid === undefined || !isProcessRunning(lastKnownAppHostPid)) {
            return;
        }

        await delay(250);
    }

    const runningAppHost = getRunningAppHostFromState(appHostPath);
    throw new Error(`Timed out after ${timeoutMs}ms waiting for AppHost process ${runningAppHost?.appHostPid ?? lastKnownAppHostPid ?? '<unknown>'} to exit ${actionDescription} ${path.dirname(appHostPath)}.`);
}

async function waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath: string, timeoutMs: number, knownAppHostPid: number | undefined, actionDescription: string): Promise<void> {
    try {
        await waitForNoRunningAppHostPath(appHostPath, timeoutMs, knownAppHostPid, actionDescription);
    }
    catch (error) {
        const runningAppHost = await getRunningAppHostAccordingToCli(appHostPath);
        // The extension state file can lag behind the CLI registry after stopDebugging:
        // it may still contain an AppHost PID even though aspire ps has already dropped
        // the AppHost. At that point the PID may be stale/reused, so don't SIGTERM it.
        if (!runningAppHost) {
            return;
        }

        if (!isProcessRunning(runningAppHost.appHostPid)) {
            throw error;
        }

        await stopProcess(runningAppHost.appHostPid, 30000);
        await waitForNoRunningAppHostPath(appHostPath, 5000, runningAppHost.appHostPid, actionDescription);
    }
}

function getRunningAppHostFromState(appHostPath: string) {
    const state = readStateFile().state;
    return state.workspaceAppHost && isSamePath(state.workspaceAppHost.appHostPath, appHostPath)
        ? state.workspaceAppHost
        : state.appHosts.find(candidate => isSamePath(candidate.appHostPath, appHostPath));
}

async function waitForProcessExit(pid: number, timeoutMs: number): Promise<void> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        if (!isProcessRunning(pid)) {
            return;
        }

        await delay(250);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for process ${pid} to exit.`);
}

async function stopProcess(pid: number, timeoutMs: number): Promise<void> {
    try {
        process.kill(pid, 'SIGTERM');
    }
    catch (error) {
        if (error instanceof Error && 'code' in error && error.code === 'ESRCH') {
            return;
        }

        throw error;
    }

    await waitForProcessExit(pid, timeoutMs);
}

function isProcessRunning(pid: number): boolean {
    if (!Number.isInteger(pid) || pid <= 0) {
        return false;
    }

    try {
        process.kill(pid, 0);
        return true;
    } catch (error) {
        return error instanceof Error && 'code' in error && error.code === 'EPERM';
    }
}

function getAppHostSdkVersion(): string {
    if (process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION) {
        return process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
    }

    const versionsProps = fs.readFileSync(path.join(getRepoRoot(), 'eng', 'Versions.props'), 'utf8');
    const major = getXmlProperty(versionsProps, 'MajorVersion');
    const minor = getXmlProperty(versionsProps, 'MinorVersion');
    const patch = getXmlProperty(versionsProps, 'PatchVersion');
    const prerelease = getXmlProperty(versionsProps, 'PreReleaseVersionLabel');
    return `${major}.${minor}.${patch}-${prerelease}`;
}

function getXmlProperty(xml: string, name: string): string {
    const match = xml.match(new RegExp(`<${name}>([^<]+)</${name}>`));
    if (!match) {
        throw new Error(`Unable to find ${name} in eng/Versions.props.`);
    }

    return match[1];
}

function getWorkspaceAppHostConfigPath(): string {
    return path.join(getWorkspaceRoot(), 'aspire.config.json');
}

function getLegacyAspireSettingsPath(): string {
    return path.join(getWorkspaceRoot(), '.aspire', 'settings.json');
}

function writeCliWrapper(
    name: string,
    options: { configInfoJson?: unknown; configInfoExitCode?: number; configInfoStderr?: string },
): string {
    const wrapperDirectory = path.join(getWorkspaceRoot(), '.e2e-cli-wrappers');
    fs.mkdirSync(wrapperDirectory, { recursive: true });

    const scriptPath = path.join(wrapperDirectory, `${name}.js`);
    fs.writeFileSync(scriptPath, `#!/usr/bin/env node
const { spawnSync } = require('child_process');
const realCli = ${JSON.stringify(getCliPath())};
const args = process.argv.slice(2);

if (args.includes('--include-disabled-commands')) {
  console.error('simulated old CLI does not support --include-disabled-commands');
  process.exit(123);
}

if (args.length === 3 && args[0] === 'config' && args[1] === 'info' && args[2] === '--json') {
${options.configInfoJson === undefined
        ? `  console.error(${JSON.stringify(options.configInfoStderr ?? 'config info is not available')});
  process.exit(${options.configInfoExitCode ?? 1});`
        : `  console.log(${JSON.stringify(JSON.stringify(options.configInfoJson))});
  process.exit(0);`}
}

const result = spawnSync(realCli, args, {
  cwd: process.cwd(),
  env: process.env,
  stdio: 'inherit',
  shell: false,
});

if (result.error) {
  console.error(result.error.stack || result.error.message);
  process.exit(1);
}

process.exit(result.status ?? (result.signal ? 1 : 0));
`);
    fs.chmodSync(scriptPath, 0o755);

    if (process.platform === 'win32') {
        const wrapperPath = path.join(wrapperDirectory, `${name}.cmd`);
        fs.writeFileSync(wrapperPath, `@echo off\r\n"${process.execPath}" "${scriptPath}" %*\r\n`);
        return wrapperPath;
    }

    const wrapperPath = path.join(wrapperDirectory, name);
    fs.writeFileSync(wrapperPath, `#!/usr/bin/env sh\nexec ${JSON.stringify(process.execPath)} ${JSON.stringify(scriptPath)} "$@"\n`);
    fs.chmodSync(wrapperPath, 0o755);

    return wrapperPath;
}

function getPackageSourceArgs(): string[] {
    const args: string[] = [];
    args.push(...getPackageVersionArgs());
    if (process.env.ASPIRE_EXTENSION_E2E_PACKAGE_SOURCE) {
        args.push('--source', process.env.ASPIRE_EXTENSION_E2E_PACKAGE_SOURCE);
    }

    return args;
}

function getPackageVersionArgs(): string[] {
    return process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION
        ? ['--version', process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION]
        : [];
}

async function waitForPath(filePath: string, timeoutMs: number): Promise<void> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        if (fs.existsSync(filePath)) {
            return;
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${filePath} to exist.`);
}

async function waitForFileContent(filePath: string, expectedText: string, timeoutMs: number): Promise<void> {
    const started = Date.now();
    let lastContent = '<missing>';
    while (Date.now() - started < timeoutMs) {
        if (fs.existsSync(filePath)) {
            lastContent = fs.readFileSync(filePath, 'utf8');
            if (lastContent.includes(expectedText)) {
                return;
            }
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${filePath} to contain '${expectedText}'. Last content:\n${lastContent}`);
}

function isBreakpointAt(value: unknown, filePath: string, line: number): boolean {
    if (!value || typeof value !== 'object') {
        return false;
    }

    const candidate = value as { filePath?: unknown; line?: unknown; enabled?: unknown };
    return candidate.filePath === filePath && candidate.line === line && candidate.enabled === true;
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function removePath(targetPath: string, options: fs.RmOptions): void {
    const maxAttempts = process.platform === 'win32' ? 40 : 1;
    for (let attempt = 1; ; attempt++) {
        try {
            fs.rmSync(targetPath, options);
            return;
        }
        catch (error) {
            if (attempt >= maxAttempts || !isRetryableFileSystemError(error)) {
                throw error;
            }

            sleepSynchronously(250);
        }
    }
}

export function writeFileWithRetry(filePath: string, content: string): void {
    const maxAttempts = process.platform === 'win32' ? 20 : 1;
    for (let attempt = 1; ; attempt++) {
        try {
            fs.writeFileSync(filePath, content);
            return;
        }
        catch (error) {
            if (attempt >= maxAttempts || !isRetryableFileSystemError(error)) {
                throw error;
            }

            sleepSynchronously(250);
        }
    }
}

function isRetryableFileSystemError(error: unknown): boolean {
    if (process.platform !== 'win32' || !error || typeof error !== 'object') {
        return false;
    }

    const code = (error as NodeJS.ErrnoException).code;
    return code === 'EBUSY' || code === 'EPERM' || code === 'EACCES' || code === 'ENOTEMPTY';
}
