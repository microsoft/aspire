import * as childProcess from 'child_process';
import * as fs from 'fs';
import * as vscode from 'vscode';

export interface LaunchedChildProcess {
    readonly pid: number;
    readonly parentPid: number;
    readonly executable: string;
    readonly command: string;
    readonly commandLineArguments?: readonly string[];
}

export interface LaunchedChildProcessQuery {
    readonly canTrustListedProcessIdentity?: boolean;
    listProcesses(cancellationToken?: vscode.CancellationToken, timeoutMs?: number): Promise<readonly LaunchedChildProcess[]>;
    getProcess?(processId: number, cancellationToken?: vscode.CancellationToken, timeoutMs?: number): Promise<LaunchedChildProcess | undefined>;
}

export interface LaunchedChildProcessClock {
    now(): number;
    sleep(milliseconds: number, cancellationToken?: vscode.CancellationToken): Promise<void>;
}

export interface LaunchedChildProcessIdentity {
    readonly requiresDirectChild?: boolean;
    isLauncher(process: LaunchedChildProcess): boolean;
    isCandidate(process: LaunchedChildProcess): boolean;
}

export interface LaunchedChildProcessCommandRunner {
    run(command: string, args: readonly string[], cancellationToken?: vscode.CancellationToken, timeoutMs?: number): Promise<string>;
}

export interface LaunchedChildProcessFileSystem {
    readlink(path: string): Promise<string>;
    readFile(path: string): Promise<Buffer>;
}

export type LaunchedChildProcessSpawner = (
    command: string,
    args: readonly string[],
    options: childProcess.SpawnOptions,
) => childProcess.ChildProcessWithoutNullStreams;

const maxProcessListingLength = 16 * 1024 * 1024;
const windowsProcessProperties = 'ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine';
const windowsProcessQuery = `$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Get-CimInstance Win32_Process | Select-Object ${windowsProcessProperties} | ConvertTo-Json -Compress`;
const linuxDeletedExecutableMarker = ' (deleted)';

export function parsePosixProcessList(output: string): readonly LaunchedChildProcess[] {
    const processes: LaunchedChildProcess[] = [];

    for (const line of output.split(/\r?\n/)) {
        const match = /^\s*(\d+)\s+(\d+)\s*$/.exec(line);
        if (!match) {
            continue;
        }

        processes.push({
            pid: Number(match[1]),
            parentPid: Number(match[2]),
            executable: '',
            command: '',
        });
    }

    return processes;
}

export function parseWindowsProcessList(output: string): readonly LaunchedChildProcess[] {
    let parsed: unknown;
    try {
        // Windows PowerShell can still prepend U+FEFF despite setting OutputEncoding. JSON.parse
        // rejects that marker, so remove it before parsing the machine-readable response.
        parsed = JSON.parse(output.replace(/^\uFEFF/, ''));
    }
    catch {
        throw createProcessDiscoveryError();
    }

    const rows = Array.isArray(parsed) ? parsed : [parsed];
    const processes: LaunchedChildProcess[] = [];
    for (const row of rows) {
        if (typeof row !== 'object' || row === null) {
            continue;
        }

        const values = row as Record<string, unknown>;
        const executablePath = getNonEmptyString(values.ExecutablePath);
        // CIM can omit ExecutablePath. Name plus CommandLine is still usable listed identity:
        // exact-path matchers fail closed on Name, and selected ancestry is freshly queried.
        const process = createProcessInfo(
            values.ProcessId,
            values.ParentProcessId,
            executablePath ?? values.Name,
            values.CommandLine);
        if (process) {
            processes.push(process);
        }
    }

    return processes;
}

export function getProcessCommandProgram(command: string): string | undefined {
    const match = /^\s*(?:"([^"]+)"|'([^']+)'|(\S+))/.exec(command);
    return match?.[1] ?? match?.[2] ?? match?.[3];
}

export class LaunchedChildProcessResolver {
    private static readonly _defaultTimeoutMs = 30_000;
    private static readonly _defaultRetryDelayMs = 100;
    private static readonly _maximumRetryDelayMs = 1_000;

    constructor(
        private readonly _processQuery: LaunchedChildProcessQuery,
        private readonly _clock: LaunchedChildProcessClock = systemLaunchedChildProcessClock,
        options: { readonly timeoutMs?: number; readonly retryDelayMs?: number } = {},
    ) {
        this._timeoutMs = options.timeoutMs ?? LaunchedChildProcessResolver._defaultTimeoutMs;
        this._retryDelayMs = options.retryDelayMs ?? LaunchedChildProcessResolver._defaultRetryDelayMs;
    }

    private readonly _timeoutMs: number;
    private readonly _retryDelayMs: number;

    async resolveProcessId(
        launcherPid: number,
        identity: LaunchedChildProcessIdentity,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<number> {
        if (!isValidPid(launcherPid)) {
            throw createProcessDiscoveryError();
        }

        const timeoutMs = Math.max(1, this._timeoutMs);
        const deadline = this._clock.now() + timeoutMs;
        let previousCandidate: number | undefined;
        let retryDelayMs = Math.max(1, this._retryDelayMs);
        const maximumAttempts = Math.max(2, Math.ceil(timeoutMs / retryDelayMs) + 1);
        let attempts = 0;

        while (this._clock.now() <= deadline && attempts++ < maximumAttempts) {
            throwIfCancelled(cancellationToken);

            let processes: readonly LaunchedChildProcess[];
            try {
                processes = await this._processQuery.listProcesses(
                    cancellationToken,
                    Math.max(1, deadline - this._clock.now()));
            }
            catch (error) {
                if (error instanceof vscode.CancellationError || cancellationToken?.isCancellationRequested) {
                    throw new vscode.CancellationError();
                }

                processes = [];
            }

            throwIfCancelled(cancellationToken);

            let candidate: number | undefined;
            try {
                candidate = await this._findMatchingCandidate(
                    launcherPid,
                    identity,
                    processes,
                    cancellationToken,
                    deadline);
            }
            catch (error) {
                if (error instanceof vscode.CancellationError || cancellationToken?.isCancellationRequested) {
                    throw new vscode.CancellationError();
                }

                throw createProcessDiscoveryError();
            }

            if (candidate !== undefined && candidate === previousCandidate &&
                await this._verifyCandidateLineage(candidate, launcherPid, identity, cancellationToken, deadline)) {
                return candidate;
            }

            previousCandidate = candidate;
            const remainingTimeMs = deadline - this._clock.now();
            if (remainingTimeMs <= 0) {
                break;
            }

            try {
                await this._clock.sleep(Math.min(retryDelayMs, remainingTimeMs), cancellationToken);
                retryDelayMs = Math.min(
                    LaunchedChildProcessResolver._maximumRetryDelayMs,
                    retryDelayMs * 2);
            }
            catch (error) {
                if (error instanceof vscode.CancellationError || cancellationToken?.isCancellationRequested) {
                    throw new vscode.CancellationError();
                }

                throw createProcessDiscoveryError();
            }
        }

        throw createProcessDiscoveryError();
    }

    private async _findMatchingCandidate(
        launcherPid: number,
        identity: LaunchedChildProcessIdentity,
        processes: readonly LaunchedChildProcess[],
        cancellationToken: vscode.CancellationToken | undefined,
        deadline: number,
    ): Promise<number | undefined> {
        const candidatePids = findDescendantProcessIds(launcherPid, identity.requiresDirectChild === true, processes);
        if (!candidatePids) {
            return undefined;
        }

        const launcher = await this._getProcess(
            launcherPid,
            processes.find(process => process.pid === launcherPid),
            cancellationToken,
            deadline);
        if (!launcher || !identity.isLauncher(launcher)) {
            return undefined;
        }

        const candidates: number[] = [];
        for (const candidatePid of candidatePids) {
            const candidate = await this._getProcess(
                candidatePid,
                processes.find(process => process.pid === candidatePid),
                cancellationToken,
                deadline);
            if (!candidate ||
                (identity.requiresDirectChild === true && candidate.parentPid !== launcherPid)) {
                continue;
            }

            if (identity.isCandidate(candidate)) {
                candidates.push(candidate.pid);
            }
        }

        return candidates.length === 1 ? candidates[0] : undefined;
    }

    private async _verifyCandidateLineage(
        candidatePid: number,
        launcherPid: number,
        identity: LaunchedChildProcessIdentity,
        cancellationToken: vscode.CancellationToken | undefined,
        deadline: number,
    ): Promise<boolean> {
        if (!this._processQuery.getProcess) {
            return true;
        }

        if (identity.requiresDirectChild === true) {
            throwIfCancelled(cancellationToken);
            if (this._clock.now() > deadline) {
                return false;
            }

            const candidate = await this._getProcess(
                candidatePid,
                undefined,
                cancellationToken,
                deadline,
                true);
            throwIfCancelled(cancellationToken);
            if (this._clock.now() > deadline) {
                return false;
            }

            return candidate !== undefined &&
                candidate.parentPid === launcherPid &&
                identity.isCandidate(candidate);
        }

        let processId = candidatePid;
        const visited = new Set<number>();

        // POSIX process-list rows contain only PID/PPID topology, while Windows CIM rows can also
        // carry trusted identity for candidate discovery. Regardless of the listing source, re-read
        // every PID in the selected transitive ancestry from the OS immediately before returning.
        while (true) {
            if (visited.has(processId) || this._clock.now() > deadline) {
                return false;
            }

            visited.add(processId);
            const process = await this._getProcess(processId, undefined, cancellationToken, deadline);
            if (!process) {
                return false;
            }

            if (processId === candidatePid && !identity.isCandidate(process)) {
                return false;
            }

            if (processId === launcherPid) {
                return identity.isLauncher(process);
            }

            if (!isValidPid(process.parentPid)) {
                return false;
            }

            processId = process.parentPid;
        }
    }

    private async _getProcess(
        processId: number,
        topologyProcess: LaunchedChildProcess | undefined,
        cancellationToken: vscode.CancellationToken | undefined,
        deadline: number,
        requireFresh = false,
    ): Promise<LaunchedChildProcess | undefined> {
        if (!this._processQuery.getProcess ||
            (!requireFresh &&
                this._processQuery.canTrustListedProcessIdentity === true &&
                topologyProcess !== undefined &&
                topologyProcess.executable.length > 0 &&
                topologyProcess.command.length > 0)) {
            return topologyProcess;
        }

        try {
            const process = await this._processQuery.getProcess(
                processId,
                cancellationToken,
                Math.max(1, deadline - this._clock.now()));
            return process?.pid === processId ? process : undefined;
        }
        catch (error) {
            if (error instanceof vscode.CancellationError || cancellationToken?.isCancellationRequested) {
                throw new vscode.CancellationError();
            }

            return undefined;
        }
    }
}

export class SystemLaunchedChildProcessQuery implements LaunchedChildProcessQuery {
    readonly canTrustListedProcessIdentity: boolean;

    constructor(
        private readonly _platform: NodeJS.Platform = process.platform,
        private readonly _commandRunner: LaunchedChildProcessCommandRunner = new SystemLaunchedChildProcessCommandRunner(),
        private readonly _fileSystem: LaunchedChildProcessFileSystem = systemLaunchedChildProcessFileSystem,
    ) {
        this.canTrustListedProcessIdentity = this._platform === 'win32';
    }

    async listProcesses(cancellationToken?: vscode.CancellationToken, timeoutMs?: number): Promise<readonly LaunchedChildProcess[]> {
        const output = this._platform === 'win32'
            ? await this._commandRunner.run(
                'powershell.exe',
                ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', windowsProcessQuery],
                cancellationToken,
                timeoutMs)
            : await this._commandRunner.run(
                'ps',
                ['-axo', 'pid=,ppid='],
                cancellationToken,
                timeoutMs);

        return this._platform === 'win32'
            ? parseWindowsProcessList(output)
            : parsePosixProcessList(output);
    }

    async getProcess(processId: number, cancellationToken?: vscode.CancellationToken, timeoutMs?: number): Promise<LaunchedChildProcess | undefined> {
        if (!isValidPid(processId)) {
            return undefined;
        }

        if (this._platform === 'win32') {
            const output = await this._commandRunner.run(
                'powershell.exe',
                ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
                    `$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Get-CimInstance Win32_Process -Filter "ProcessId = ${processId}" | Select-Object ${windowsProcessProperties} | ConvertTo-Json -Compress`],
                cancellationToken,
                timeoutMs);
            return parseWindowsProcessList(output).find(process => process.pid === processId);
        }

        if (this._platform === 'linux') {
            return this._getLinuxProcess(processId, cancellationToken, timeoutMs);
        }

        const [parentPidOutput, executableOutput, commandOutput] = await Promise.all([
            this._commandRunner.run('ps', ['-p', String(processId), '-o', 'ppid='], cancellationToken, timeoutMs),
            this._commandRunner.run('ps', ['-p', String(processId), '-o', 'comm='], cancellationToken, timeoutMs),
            this._commandRunner.run('ps', ['-p', String(processId), '-o', 'args='], cancellationToken, timeoutMs),
        ]);
        return createProcessInfo(
            processId,
            parentPidOutput.trim(),
            executableOutput.trim(),
            commandOutput.trim());
    }

    private async _getLinuxProcess(
        processId: number,
        cancellationToken: vscode.CancellationToken | undefined,
        timeoutMs: number | undefined,
    ): Promise<LaunchedChildProcess | undefined> {
        // Procfs exposes exact details as separate kernel-owned files. `cmdline` is a NUL-separated
        // byte sequence such as `dotnet\0exec\0/repo/My Service.dll\0`; do not route it through a
        // shell or flatten it before identity matching.
        const [executable, commandLine, status] = await awaitProcessDetails(
            Promise.all([
                this._fileSystem.readlink(`/proc/${processId}/exe`),
                this._fileSystem.readFile(`/proc/${processId}/cmdline`),
                this._fileSystem.readFile(`/proc/${processId}/status`),
            ]),
            cancellationToken,
            timeoutMs);
        const parentPid = parseLinuxParentPid(status);
        if (parentPid === undefined) {
            return undefined;
        }

        const commandLineArguments = parseLinuxCommandLine(commandLine);
        return createProcessInfo(
            processId,
            parentPid,
            normalizeLinuxExecutablePath(executable),
            commandLineArguments.join(' '),
            commandLineArguments);
    }
}

export class SystemLaunchedChildProcessCommandRunner implements LaunchedChildProcessCommandRunner {
    constructor(
        private readonly _spawn: LaunchedChildProcessSpawner =
            childProcess.spawn as unknown as LaunchedChildProcessSpawner,
    ) {
    }

    run(command: string, args: readonly string[], cancellationToken?: vscode.CancellationToken, timeoutMs = 1_000): Promise<string> {
        return new Promise((resolve, reject) => {
            let completed = false;
            let cancellationRegistration: vscode.Disposable | undefined;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            let output = '';
            const process = this._spawn(command, args, {
                stdio: 'pipe',
                windowsHide: true,
            });

            const complete = (action: () => void) => {
                if (completed) {
                    return;
                }

                completed = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                cancellationRegistration?.dispose();
                action();
            };
            const fail = () => {
                // Discovery owns this short-lived `ps` or PowerShell child only. Never signal the
                // launched workload or any descendant while resolving an attach target.
                if (!process.killed) {
                    process.kill();
                }
                complete(() => reject(createProcessDiscoveryError()));
            };

            process.stdout.setEncoding('utf8');
            process.stdout.on('data', (chunk: string) => {
                if (output.length + chunk.length > maxProcessListingLength) {
                    fail();
                    return;
                }

                output += chunk;
            });
            // Drain stderr so a failed fixed query cannot block on a full pipe. Its contents may
            // include command text and are intentionally neither logged nor returned.
            process.stderr.resume();
            process.on('error', fail);
            process.on('close', exitCode => {
                if (exitCode === 0) {
                    complete(() => resolve(output));
                }
                else {
                    complete(() => reject(createProcessDiscoveryError()));
                }
            });

            cancellationRegistration = cancellationToken?.onCancellationRequested(fail);
            timeout = setTimeout(fail, Math.max(1, timeoutMs));
            if (cancellationToken?.isCancellationRequested) {
                fail();
            }
        });
    }
}

const systemLaunchedChildProcessClock: LaunchedChildProcessClock = {
    now: () => Date.now(),
    sleep: (milliseconds, cancellationToken) => new Promise<void>((resolve, reject) => {
        if (cancellationToken?.isCancellationRequested) {
            reject(new vscode.CancellationError());
            return;
        }

        let cancellationRegistration: vscode.Disposable | undefined;
        const timeout = setTimeout(() => {
            cancellationRegistration?.dispose();
            resolve();
        }, milliseconds);
        cancellationRegistration = cancellationToken?.onCancellationRequested(() => {
            clearTimeout(timeout);
            cancellationRegistration?.dispose();
            reject(new vscode.CancellationError());
        });
    }),
};

const systemLaunchedChildProcessFileSystem: LaunchedChildProcessFileSystem = {
    readlink: path => fs.promises.readlink(path),
    readFile: path => fs.promises.readFile(path),
};

export const launchedChildProcessResolver = new LaunchedChildProcessResolver(
    new SystemLaunchedChildProcessQuery());

function getNonEmptyString(value: unknown): string | undefined {
    if (typeof value !== 'string') {
        return undefined;
    }

    const trimmedValue = value.trim();
    return trimmedValue.length > 0 ? trimmedValue : undefined;
}

function createProcessInfo(
    pidValue: unknown,
    parentPidValue: unknown,
    executableValue: unknown,
    commandValue: unknown,
    commandLineArguments?: readonly string[],
): LaunchedChildProcess | undefined {
    const pid = parsePid(pidValue);
    const parentPid = parseParentPid(parentPidValue);
    const executable = typeof executableValue === 'string' ? executableValue.trim() : '';
    const command = typeof commandValue === 'string' ? commandValue.trim() : '';
    if (pid === undefined || parentPid === undefined || executable.length === 0) {
        return undefined;
    }

    return {
        pid,
        parentPid,
        executable,
        command,
        ...(commandLineArguments ? { commandLineArguments } : {}),
    };
}

function parseLinuxCommandLine(commandLine: Buffer): readonly string[] {
    const argumentsList = commandLine.toString('utf8').split('\0');
    if (argumentsList.at(-1) === '') {
        argumentsList.pop();
    }

    return argumentsList;
}

function parseLinuxParentPid(status: Buffer): number | undefined {
    const match = /^PPid:\s*(\d+)\s*$/m.exec(status.toString('utf8'));
    return match ? parseParentPid(match[1]) : undefined;
}

function normalizeLinuxExecutablePath(executable: string): string {
    // `/proc/<pid>/exe` reports an unlinked executable as `/path/app (deleted)`. Remove only
    // the kernel's exact trailing marker so a filename that contains those characters elsewhere
    // remains a distinct executable identity.
    return executable.endsWith(linuxDeletedExecutableMarker)
        ? executable.slice(0, -linuxDeletedExecutableMarker.length)
        : executable;
}

function awaitProcessDetails<T>(
    details: Promise<T>,
    cancellationToken: vscode.CancellationToken | undefined,
    timeoutMs: number | undefined,
): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        let completed = false;
        let cancellationRegistration: vscode.Disposable | undefined;
        const timeout = setTimeout(
            () => complete(() => reject(createProcessDiscoveryError())),
            Math.max(1, timeoutMs ?? 30_000));
        const complete = (action: () => void) => {
            if (completed) {
                return;
            }

            completed = true;
            clearTimeout(timeout);
            cancellationRegistration?.dispose();
            action();
        };

        // The procfs reads have already started. Observe both outcomes before checking
        // cancellation so a cancelled caller cannot leave the aggregate promise unobserved.
        details.then(
            result => complete(() => resolve(result)),
            error => complete(() => reject(error)));

        cancellationRegistration = cancellationToken?.onCancellationRequested(
            () => complete(() => reject(new vscode.CancellationError())));
        if (cancellationToken?.isCancellationRequested) {
            complete(() => reject(new vscode.CancellationError()));
            return;
        }
    });
}

function findDescendantProcessIds(
    launcherPid: number,
    requiresDirectChild: boolean,
    processes: readonly LaunchedChildProcess[],
): readonly number[] | undefined {
    const processById = new Map<number, LaunchedChildProcess>();
    const childrenByParentId = new Map<number, LaunchedChildProcess[]>();
    for (const process of processes) {
        if (!isValidPid(process.pid) || !Number.isInteger(process.parentPid) || process.parentPid < 0 || processById.has(process.pid)) {
            return undefined;
        }

        processById.set(process.pid, process);
        const children = childrenByParentId.get(process.parentPid) ?? [];
        children.push(process);
        childrenByParentId.set(process.parentPid, children);
    }

    if (!processById.has(launcherPid)) {
        return undefined;
    }

    const descendants = [...(childrenByParentId.get(launcherPid) ?? [])];
    if (requiresDirectChild) {
        return descendants.map(descendant => descendant.pid);
    }

    const descendantsIds: number[] = [];
    const visitedProcessIds = new Set([launcherPid]);
    for (let index = 0; index < descendants.length; index++) {
        const descendant = descendants[index];
        if (visitedProcessIds.has(descendant.pid)) {
            return undefined;
        }

        visitedProcessIds.add(descendant.pid);
        descendantsIds.push(descendant.pid);
        descendants.push(...(childrenByParentId.get(descendant.pid) ?? []));
    }

    return descendantsIds;
}

function parsePid(value: unknown): number | undefined {
    if (typeof value === 'number' && isValidPid(value)) {
        return value;
    }

    if (typeof value !== 'string') {
        return undefined;
    }

    const pid = Number(value);
    return isValidPid(pid) ? pid : undefined;
}

function parseParentPid(value: unknown): number | undefined {
    if (typeof value === 'number' && Number.isInteger(value) && value >= 0) {
        return value;
    }

    if (typeof value !== 'string') {
        return undefined;
    }

    const pid = Number(value);
    return Number.isInteger(pid) && pid >= 0 ? pid : undefined;
}

function isValidPid(value: number): boolean {
    return Number.isInteger(value) && value > 0;
}

function throwIfCancelled(cancellationToken?: vscode.CancellationToken): void {
    if (cancellationToken?.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}

function createProcessDiscoveryError(): Error {
    return new Error('Unable to resolve the running application process.');
}
