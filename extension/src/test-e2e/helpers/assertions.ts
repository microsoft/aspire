import * as fs from 'fs';
import * as http from 'http';
import * as https from 'https';
import * as path from 'path';
import type { AspireAppHostState as AppHostState, AspireDebugSessionState, AspireExtensionE2EControlStatus as ExtensionE2EControlStatus, AspireExtensionE2EStateFile as ExtensionE2EStateFile, AspireExtensionStateSnapshot as ExtensionStateSnapshot, AspireResourceState as ResourceState } from '../../types/extensionApi';
import { getControlFilePath, getPrimaryAppHostProjectPath, getStateFilePath, getWorkspaceRoot } from './paths';

type CommandInvocation = ExtensionE2EStateFile['commandInvocations'][number];
interface Deadline {
    readonly started: number;
    readonly timeoutMs: number;
}

let controlRevision = Date.now();
let workspaceFolderOpened = false;

export async function waitForRepositoryIdle(timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => file.state.isWorkspaceAppHostDiscoveryComplete && !file.state.isRepositoryLoading, 'repository to become idle', timeoutMs);
}

export async function waitForWorkspaceAppHost(timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    const deadline = createDeadline(timeoutMs);
    await ensureWorkspaceFolderOpen(deadline);
    return await waitForExtensionState(
        file => file.state.workspaceAppHostCandidatePaths.some(candidate => isSamePath(candidate, getPrimaryAppHostProjectPath())),
        'workspace AppHost candidate',
        getRemainingTimeout(deadline, 'workspace AppHost candidate'));
}

export async function waitForSelectedWorkspaceAppHost(appHostPath = getPrimaryAppHostProjectPath(), timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    const deadline = createDeadline(timeoutMs);
    await ensureWorkspaceFolderOpen(deadline);
    return await waitForExtensionState(
        file => file.state.workspaceAppHostPath !== undefined && isSamePath(file.state.workspaceAppHostPath, appHostPath),
        `selected workspace AppHost '${appHostPath}'`,
        getRemainingTimeout(deadline, `selected workspace AppHost '${appHostPath}'`));
}

export async function waitForRunningAppHost(timeoutMs = 180000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => findRunningAppHost(file.state) !== undefined, 'running AppHost', timeoutMs);
}

export async function waitForAppHostLaunching(appHostPath = getPrimaryAppHostProjectPath(), timeoutMs = 60000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(
        file => file.state.launchingPaths.some(launchingPath => isSamePath(launchingPath, appHostPath)),
        `AppHost '${appHostPath}' to enter launching state`,
        timeoutMs);
}

export async function waitForNoRunningAppHost(timeoutMs = 90000, appHostPath = getPrimaryAppHostProjectPath()): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(
        file => findRunningAppHost(file.state, appHostPath) === undefined && !file.state.launchingPaths.some(launchingPath => isSamePath(launchingPath, appHostPath)),
        `AppHost '${appHostPath}' to stop`,
        timeoutMs);
}

export async function waitForResource(resourceName: string, timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => getResources(file.state).some(resource => isResourceMatch(resource, resourceName)), `resource '${resourceName}'`, timeoutMs);
}

export async function waitForResourceState(resourceName: string, states: readonly string[], timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => getResources(file.state).some(resource => isResourceMatch(resource, resourceName) && resource.state !== null && states.includes(resource.state)), `resource '${resourceName}' state ${states.join(' or ')}`, timeoutMs);
}

export async function waitForDashboardUrl(timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => typeof file.dashboardUrl === 'string' && file.dashboardUrl.length > 0, 'dashboard URL', timeoutMs);
}

export async function waitForDebugSessionStartup(appHostPath = getPrimaryAppHostProjectPath(), timeoutMs = 180000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => file.state.debugSessions.some(session => isDebugSessionForAppHost(session, appHostPath) && session.startupCompleted), 'debug AppHost startup', timeoutMs);
}

export async function waitForDebugDashboardUrl(appHostPath = getPrimaryAppHostProjectPath(), timeoutMs = 120000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => file.state.debugSessions.some(session => isDebugSessionForAppHost(session, appHostPath) && typeof session.dashboardUrl === 'string' && session.dashboardUrl.length > 0), 'debug dashboard URL', timeoutMs);
}

export async function waitForHttpText(url: string, expectedText: string, timeoutMs = 120000, descriptionUrl = sanitizeUrlForDiagnostics(url)): Promise<string> {
    const started = Date.now();
    let lastError: string | undefined;

    while (Date.now() - started < timeoutMs) {
        try {
            const body = await getUrlText(url);
            if (body.includes(expectedText)) {
                return expectedText;
            }

            lastError = `response did not contain '${expectedText}': ${body}`;
        }
        catch (error) {
            lastError = error instanceof Error ? error.message : String(error);
        }

        await new Promise(resolve => setTimeout(resolve, 500));
    }

    throw new Error(`Timed out waiting for ${descriptionUrl} to return '${expectedText}'. Last error: ${lastError ?? '<none>'}`);
}

export async function waitForNoDebugSessions(timeoutMs = 90000): Promise<ExtensionE2EStateFile> {
    return await waitForExtensionState(file => file.state.debugSessions.length === 0, 'debug sessions to stop', timeoutMs);
}

export async function waitForCommandOutcome(command: string, outcome: CommandInvocation['outcome'], timeoutMs = 60000, afterInvocationSequence = 0): Promise<CommandInvocation> {
    const file = await waitForExtensionState(stateFile => stateFile.commandInvocations.some(event => event.command === command && event.sequence > afterInvocationSequence && event.outcome === outcome), `${command} ${outcome} outcome`, timeoutMs);
    const event = file.commandInvocations.find(candidate => candidate.command === command && candidate.sequence > afterInvocationSequence && candidate.outcome === outcome);
    if (!event) {
        throw new Error(`Command '${command}' did not produce '${outcome}' even though the state predicate matched.`);
    }

    return event;
}

export function getCommandInvocationCount(command?: string): number {
    const file = readStateFile();
    const matchingEvents = command
        ? file.commandInvocations.filter(event => event.command === command)
        : file.commandInvocations;

    return Math.max(0, ...matchingEvents.map(event => event.sequence));
}

export async function waitForTerminalCommand(
    predicate: (event: ExtensionE2EStateFile['terminalCommands'][number]) => boolean,
    description: string,
    timeoutMs = 60000,
    afterCommandSequence = 0,
): Promise<ExtensionE2EStateFile['terminalCommands'][number]> {
    const file = await waitForExtensionState(stateFile => stateFile.terminalCommands.some(event => event.sequence > afterCommandSequence && predicate(event)), description, timeoutMs);
    const event = file.terminalCommands.find(candidate => candidate.sequence > afterCommandSequence && predicate(candidate));
    if (!event) {
        throw new Error(`Terminal command '${description}' was not found even though the state predicate matched.`);
    }

    return event;
}

export function getTerminalCommandCount(): number {
    return Math.max(0, ...readStateFile().terminalCommands.map(event => event.sequence));
}

export async function waitForDebugLaunch(
    predicate: (event: ExtensionE2EStateFile['debugLaunches'][number]) => boolean,
    description: string,
    timeoutMs = 60000,
    afterLaunchSequence = 0,
): Promise<ExtensionE2EStateFile['debugLaunches'][number]> {
    const file = await waitForExtensionState(stateFile => stateFile.debugLaunches.some(event => event.sequence > afterLaunchSequence && predicate(event)), description, timeoutMs);
    const event = file.debugLaunches.find(candidate => candidate.sequence > afterLaunchSequence && predicate(candidate));
    if (!event) {
        throw new Error(`Debug launch '${description}' was not found even though the state predicate matched.`);
    }

    return event;
}

export function getDebugLaunchCount(): number {
    return Math.max(0, ...readStateFile().debugLaunches.map(event => event.sequence));
}

export async function waitForDebugConsoleOutput(expectedText: string, appHostPath = getPrimaryAppHostProjectPath(), timeoutMs = 60000): Promise<ExtensionE2EStateFile['debugConsoleOutputs'][number]> {
    const file = await waitForExtensionState(
        stateFile => stateFile.debugConsoleOutputs.some(event =>
            event.appHostPath !== undefined &&
            isSamePath(event.appHostPath, appHostPath) &&
            event.output.includes(expectedText)),
        `debug console output containing '${expectedText}'`,
        timeoutMs);
    const event = file.debugConsoleOutputs.find(candidate =>
        candidate.appHostPath !== undefined &&
        isSamePath(candidate.appHostPath, appHostPath) &&
        candidate.output.includes(expectedText));
    if (!event) {
        throw new Error(`Debug console output containing '${expectedText}' was not found even though the state predicate matched.`);
    }

    return event;
}

export function getTreeAppHostLabel(state: ExtensionStateSnapshot): string {
    return state.workspaceAppHostName ?? path.basename(getPrimaryAppHostProjectPath());
}

export function getResources(state: ExtensionStateSnapshot): readonly ResourceState[] {
    const runningAppHost = findRunningAppHost(state);
    return state.workspaceResources.length > 0 ? state.workspaceResources : runningAppHost?.resources ?? [];
}

export function findResource(state: ExtensionStateSnapshot, resourceName: string): ResourceState | undefined {
    return getResources(state).find(resource => isResourceMatch(resource, resourceName));
}

export function findRunningAppHost(state: ExtensionStateSnapshot, appHostPath = getPrimaryAppHostProjectPath()): AppHostState | undefined {
    return state.workspaceAppHost && isSamePath(state.workspaceAppHost.appHostPath, appHostPath)
        ? state.workspaceAppHost
        : state.appHosts.find(appHost => isSamePath(appHost.appHostPath, appHostPath));
}

export async function waitForExtensionState(
    predicate: (file: ExtensionE2EStateFile) => boolean,
    description: string,
    timeoutMs = 60000,
): Promise<ExtensionE2EStateFile> {
    const started = Date.now();
    let lastState: string | undefined;
    let lastError: Error | undefined;

    while (Date.now() - started < timeoutMs) {
        try {
            const file = readStateFile();
            lastState = JSON.stringify(file, undefined, 2);
            if (predicate(file)) {
                return file;
            }
        }
        catch (error) {
            lastError = error instanceof Error ? error : new Error(String(error));
        }

        await delay(200);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}.\nLast error: ${lastError?.message ?? '<none>'}\nLast state: ${lastState ?? '<none>'}`);
}

export function readStateFile(): ExtensionE2EStateFile {
    const maxAttempts = process.platform === 'win32' ? 10 : 1;
    for (let attempt = 1; ; attempt++) {
        try {
            return JSON.parse(fs.readFileSync(getStateFilePath(), 'utf8')) as ExtensionE2EStateFile;
        }
        catch (error) {
            if (attempt >= maxAttempts || !isRetryableStateFileReadError(error)) {
                throw error;
            }

            sleepSynchronously(25);
        }
    }
}

async function ensureWorkspaceFolderOpen(deadline: Deadline): Promise<void> {
    if (workspaceFolderOpened) {
        return;
    }

    const expectedPath = getWorkspaceRoot();
    if (await tryWaitForWorkspaceFolder(expectedPath, deadline, 5000)) {
        workspaceFolderOpened = true;
        return;
    }

    const openWorkspaceStatus = await applyE2eControl(
        { command: { name: 'openWorkspaceFolder', folderPath: expectedPath } },
        'started',
        getRemainingTimeout(deadline, 'openWorkspaceFolder control to start', 10000));
    const openWorkspaceRevision = openWorkspaceStatus.revision;
    if (await tryWaitForWorkspaceFolder(expectedPath, deadline, 120000, openWorkspaceRevision)) {
        workspaceFolderOpened = true;
        return;
    }

    throwIfControlFailed(openWorkspaceRevision);
    const folders = await getWorkspaceFolders(getRemainingTimeout(deadline, 'workspace folder diagnostics', 10000))
        .catch(error => `failed to query workspace folders: ${error instanceof Error ? error.message : String(error)}`);
    throw new Error(`Timed out after ${deadline.timeoutMs}ms waiting for VS Code to open E2E workspace '${expectedPath}'. Last workspace folders: ${JSON.stringify(folders)}`);
}

async function tryWaitForWorkspaceFolder(expectedPath: string, deadline: Deadline, timeoutMs: number, openWorkspaceRevision?: number): Promise<boolean> {
    const endsAt = Date.now() + getRemainingTimeout(deadline, `VS Code workspace folder '${expectedPath}'`, timeoutMs);
    while (Date.now() < endsAt) {
        if (openWorkspaceRevision !== undefined) {
            throwIfControlFailed(openWorkspaceRevision);
        }

        const queryTimeoutMs = Math.min(10000, Math.max(1, endsAt - Date.now()), getRemainingTimeout(deadline, 'workspace folder query'));
        const folders = await getWorkspaceFolders(queryTimeoutMs).catch(() => []);
        if (folders.some(folder => folder.fileName && isSamePath(folder.fileName, expectedPath))) {
            return true;
        }

        await delay(Math.min(200, Math.max(1, endsAt - Date.now())));
    }

    return false;
}

async function getWorkspaceFolders(timeoutMs: number): Promise<Array<{ fileName?: string }>> {
    const status = await applyE2eControl({ command: { name: 'getWorkspaceFolders' } }, 'applied', timeoutMs);
    return Array.isArray(status.result) ? status.result as Array<{ fileName?: string }> : [];
}

function throwIfControlFailed(revision: number): void {
    const control = readStateFile().control;
    if (control?.revision === revision && control.status === 'error') {
        throw new Error(`Failed to apply E2E control revision ${revision}: ${control.errorMessage ?? '<unknown>'}`);
    }
}

export async function applyE2eControl(payload: Record<string, unknown>, waitFor: 'started' | 'applied' = 'applied', timeoutMs = 10000): Promise<ExtensionE2EControlStatus> {
    const controlFilePath = getControlFilePath();
    if (!controlFilePath) {
        return { revision: -1, status: 'applied' };
    }

    const revision = ++controlRevision;
    fs.writeFileSync(controlFilePath, JSON.stringify({ revision, ...payload }, undefined, 2));
    const stateFile = await waitForExtensionState(
        file => file.control?.revision === revision && (file.control.status === 'error' || file.control.status === 'applied' || (waitFor === 'started' && file.control.status === 'started')),
        `E2E control revision ${revision}`,
        timeoutMs);

    if (stateFile.control?.status === 'error') {
        throw new Error(`Failed to apply E2E control revision ${revision}: ${stateFile.control.errorMessage ?? '<unknown>'}`);
    }

    if (!stateFile.control) {
        throw new Error(`E2E control revision ${revision} completed without reporting status.`);
    }

    return stateFile.control;
}

function createDeadline(timeoutMs: number): Deadline {
    return {
        started: Date.now(),
        timeoutMs,
    };
}

function getRemainingTimeout(deadline: Deadline, description: string, capMs?: number): number {
    const remaining = deadline.timeoutMs - (Date.now() - deadline.started);
    if (remaining <= 0) {
        throw new Error(`Timed out after ${deadline.timeoutMs}ms waiting for ${description}.`);
    }

    return capMs === undefined ? remaining : Math.min(remaining, capMs);
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

export function isSamePath(left: string, right: string): boolean {
    const resolvedLeft = canonicalizePath(left);
    const resolvedRight = canonicalizePath(right);
    return process.platform === 'win32'
        ? resolvedLeft.toLowerCase() === resolvedRight.toLowerCase()
        : resolvedLeft === resolvedRight;
}

function canonicalizePath(value: string): string {
    const resolved = path.resolve(value);
    return fs.existsSync(resolved) ? fs.realpathSync.native(resolved) : resolved;
}

function isRetryableStateFileReadError(error: unknown): boolean {
    if (process.platform !== 'win32') {
        return false;
    }

    if (error instanceof SyntaxError) {
        return true;
    }

    if (!error || typeof error !== 'object' || !('code' in error)) {
        return false;
    }

    return error.code === 'EPERM' || error.code === 'EACCES' || error.code === 'EBUSY' || error.code === 'ENOENT';
}

function isDebugSessionForAppHost(session: AspireDebugSessionState, appHostPath: string): boolean {
    return session.appHostPath !== undefined && isSamePath(session.appHostPath, appHostPath);
}

function isResourceMatch(resource: ResourceState, resourceName: string): boolean {
    return resource.name === resourceName || resource.displayName === resourceName;
}

function getUrlText(url: string, redirectLimit = 5, cookies: string[] = []): Promise<string> {
    return new Promise((resolve, reject) => {
        const parsed = new URL(url);
        const headers: Record<string, string> = {
            'accept-encoding': 'identity',
        };
        if (cookies.length > 0) {
            headers.cookie = cookies.join('; ');
        }

        const handleResponse = (response: http.IncomingMessage): void => {
            const setCookie = response.headers['set-cookie'] ?? [];
            const nextCookies = [...cookies, ...setCookie.map(cookie => cookie.split(';', 1)[0])];
            if (response.statusCode && response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
                if (redirectLimit === 0) {
                    reject(new Error('too many redirects'));
                    return;
                }

                response.resume();
                response.on('end', () => {
                    getUrlText(new URL(response.headers.location!, parsed).toString(), redirectLimit - 1, nextCookies).then(resolve, reject);
                });
                return;
            }

            const chunks: Buffer[] = [];
            response.on('data', chunk => chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)));
            response.on('end', () => {
                const body = Buffer.concat(chunks).toString('utf8');
                if (response.statusCode && response.statusCode >= 400) {
                    reject(new Error(`HTTP ${response.statusCode}: ${body}`));
                    return;
                }

                resolve(body);
            });
        };
        const request = parsed.protocol === 'https:'
            ? https.get(parsed, { headers, rejectUnauthorized: false }, handleResponse)
            : http.get(parsed, { headers }, handleResponse);

        request.on('error', reject);
        request.setTimeout(10000, () => {
            request.destroy(new Error('request timed out'));
        });
    });
}

function sanitizeUrlForDiagnostics(url: string): string {
    try {
        const parsed = new URL(url);
        if (parsed.searchParams.has('t')) {
            parsed.searchParams.set('t', '<redacted>');
        }

        return parsed.toString();
    } catch {
        return '<invalid URL>';
    }
}

function sleepSynchronously(milliseconds: number): void {
    const buffer = new SharedArrayBuffer(4);
    Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}
