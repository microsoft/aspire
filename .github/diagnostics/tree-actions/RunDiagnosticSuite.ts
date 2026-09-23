// TEMPORARY #20103 controller. Runs inside actions/github-script so the official
// artifact action can publish checkpoints before this runner finishes or is lost.
import * as fs from 'node:fs';
import * as path from 'node:path';
import { spawn, type ChildProcess } from 'node:child_process';

export interface DiagnosticOptions {
    extensionRoot: string;
    outputRoot: string;
    temporaryRoot: string;
    toolsRoot: string;
    nodePath: string;
    uploadActionPath: string;
    runnerIndex: string;
    runAttempt: string;
    sourceSha: string;
    workflowSha: string;
}

export interface DiagnosticResult {
    runnerExitCode: number | null;
    observerExitCode: number | null;
    checkpointCount: number;
    diagnosticErrors: string[];
}

export function observerReady(value: unknown, now = Date.now()): boolean {
    if (value === null || typeof value !== 'object'
        || !('state' in value) || value.state !== 'running'
        || !('dcpExecutablePresent' in value) || value.dcpExecutablePresent !== true
        || !('heartbeatAtUtc' in value) || typeof value.heartbeatAtUtc !== 'string'
        || !('counts' in value) || value.counts === null || typeof value.counts !== 'object'
        || !('successfulSamples' in value.counts) || typeof value.counts.successfulSamples !== 'number') {
        return false;
    }
    const age = now - Date.parse(value.heartbeatAtUtc);
    return value.counts.successfulSamples > 0 && age >= 0 && age < 15000;
}

export function redact(text: string): string {
    // Match the existing runner's token redaction, including raw JSON and URLs
    // emitted before its finally block: /login?t=abc..., "token":"abc...".
    return text
        .replace(/\/login\?t=[^"'\s<>\\)]+/gi, '/login?t=<redacted>')
        .replace(/([?&]t=)[^"'\s<>\\)&]+/gi, '$1<redacted>')
        .replace(/(Setting up RPC server with token: )[^\r\n]+/gi, '$1<redacted>')
        .replace(/(token["']?\s*[:=]\s*["']?)[A-Za-z0-9+/=._-]{16,}/gi, '$1<redacted>')
        .replace(/(Authorization:\s*(?:Bearer|Basic)\s+)[^\s"']+/gi, '$1<redacted>')
        .replace(/([?&]sig=)[^&\s"']+/gi, '$1<redacted>');
}

export function workloadEnvironment(environment: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
    const result: NodeJS.ProcessEnv = {};
    for (const [key, value] of Object.entries(environment)) {
        const upperKey = key.toUpperCase();
        // Runtime credentials stay in the parent JavaScript action. Neither the
        // app nor its diagnostic observer needs repository/artifact authority.
        if (/(?:TOKEN|PASSWORD|SECRET|CREDENTIAL|AUTH_KEY)/i.test(key)
            || upperKey.startsWith('INPUT_') || upperKey.startsWith('ACTIONS_')) {
            continue;
        }
        result[key] = value;
    }
    return result;
}

export function snapshotText(source: string, destination: string, maxBytes = 4 * 1024 * 1024): void {
    const handle = fs.openSync(source, 'r');
    try {
        const size = fs.fstatSync(handle).size;
        const offset = Math.max(0, size - maxBytes);
        const buffer = Buffer.alloc(size - offset);
        const length = fs.readSync(handle, buffer, 0, buffer.length, offset);
        const prefix = offset > 0 ? `[diagnostic snapshot truncated; omitted ${offset} bytes]\n` : '';
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        let content = buffer.subarray(0, length).toString('utf8');
        if (offset > 0) {
            const newline = content.indexOf('\n');
            content = newline >= 0 ? content.slice(newline + 1) : '[partial single-line content omitted]\n';
        }
        fs.writeFileSync(destination, prefix + redact(content), 'utf8');
    } finally {
        fs.closeSync(handle);
    }
}

function filesUnder(root: string, extensions: ReadonlySet<string>): string[] {
    if (!fs.existsSync(root)) {
        return [];
    }
    const files: string[] = [];
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
        const fullPath = path.join(root, entry.name);
        if (entry.isSymbolicLink()) {
            continue;
        }
        if (entry.isDirectory()) {
            files.push(...filesUnder(fullPath, extensions));
        } else if (entry.isFile() && extensions.has(path.extname(entry.name))) {
            files.push(fullPath);
        }
    }
    return files;
}

function childExit(child: ChildProcess): Promise<number | null> {
    return new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', (code, signal) => {
            if (signal) {
                reject(new Error(`Diagnostic child ${child.pid} exited from signal ${signal}.`));
            } else {
                resolve(code);
            }
        });
    });
}

function pipeLog(child: ChildProcess, filename: string, mirror: boolean): () => void {
    const flushers: Array<() => void> = [];
    for (const stream of [child.stdout, child.stderr]) {
        let partial = '';
        stream?.on('data', (chunk: Buffer) => {
            partial += chunk.toString('utf8');
            const lines = partial.split(/\r?\n/);
            partial = lines.pop() ?? '';
            for (const line of lines) {
                const safe = redact(line) + '\n';
                fs.appendFileSync(filename, safe, 'utf8');
                if (mirror && (line.startsWith('[tree-actions-diag]') || /passing|failing|Error:/.test(line))) {
                    console.log(safe.trimEnd());
                }
            }
        });
        flushers.push(() => {
            if (partial) {
                fs.appendFileSync(filename, redact(partial) + '\n', 'utf8');
                partial = '';
            }
            stream?.destroy();
        });
    }
    // Detached descendants can inherit these pipes after their direct parent
    // exits. Do not let that keep the diagnostic action alive indefinitely.
    return () => { for (const flush of flushers) { flush(); } };
}

export async function runDiagnostics(options: DiagnosticOptions): Promise<DiagnosticResult> {
    for (const directory of [options.outputRoot, options.temporaryRoot]) {
        fs.mkdirSync(directory, { recursive: true });
    }
    if (!fs.existsSync(options.uploadActionPath)) {
        throw new Error('The pinned official upload-artifact entry point is unavailable.');
    }
    if (!process.env.ACTIONS_RUNTIME_TOKEN || !process.env.ACTIONS_RESULTS_URL) {
        throw new Error('This controller must run inside a JavaScript action with artifact runtime credentials.');
    }
    const observations = path.join(options.outputRoot, 'observations');
    fs.mkdirSync(observations, { recursive: true });
    const observerStop = path.join(options.outputRoot, 'observer.stop');
    const result: DiagnosticResult = {
        runnerExitCode: null, observerExitCode: null, checkpointCount: 0, diagnosticErrors: [],
    };
    const shard = `tree-actions-stress-r${options.runnerIndex}`;
    const resultsDirectory = path.join(options.extensionRoot, '.test-results', 'e2e', shard);
    const runnerLog = path.join(options.outputRoot, 'runner.log');
    const observerLog = path.join(options.outputRoot, 'observer.log');
    let observer: ChildProcess | undefined;
    let runner: ChildProcess | undefined;
    let observerFinished: Promise<number | null> | undefined;
    let runnerFinished: Promise<number | null> | undefined;
    let runnerDone = false;
    let sequence = 0;
    let finishObserverLog: (() => void) | undefined;
    let finishRunnerLog: (() => void) | undefined;

    const checkpoint = async (reason: string): Promise<void> => {
        if (++sequence > 150) {
            throw new Error('Diagnostic checkpoint cap reached; no further workload should be started.');
        }
        const checkpointDirectory = path.join(options.outputRoot, 'checkpoints', String(sequence).padStart(3, '0'));
        fs.mkdirSync(checkpointDirectory, { recursive: true });
        const snapshotErrors: Array<{ file: string; error: string }> = [];
        const sources: Array<{ source: string; prefix: string }> = [
            { source: observations, prefix: 'observations' },
            { source: resultsDirectory, prefix: 'extension-results' },
        ];
        // Live AppHost/CLI logs are inside this controller's own aev-* roots.
        // Do not traverse package caches, homes belonging to other apps, or dumps.
        for (const entry of fs.readdirSync(options.temporaryRoot, { withFileTypes: true })) {
            if (entry.isDirectory() && entry.name.startsWith('aev-')) {
                sources.push({
                    source: path.join(options.temporaryRoot, entry.name, 'aspire-home', 'logs'),
                    prefix: `cli/${entry.name}`,
                });
            }
        }
        for (const { source, prefix } of sources) {
            let files: string[];
            try {
                files = filesUnder(source, new Set(['.json', '.jsonl', '.log', '.txt']));
            } catch (error) {
                snapshotErrors.push({ file: prefix, error: error instanceof Error ? redact(error.message) : 'Enumeration failed' });
                continue;
            }
            for (const file of files) {
                try {
                    snapshotText(file, path.join(checkpointDirectory, prefix, path.relative(source, file)));
                } catch (error) {
                    snapshotErrors.push({
                        file: path.join(prefix, path.relative(source, file)),
                        error: error instanceof Error ? redact(error.message) : 'Unknown error',
                    });
                }
            }
        }
        for (const file of [
            runnerLog, observerLog,
            path.join(options.outputRoot, 'environment.json'),
            path.join(options.outputRoot, 'versions.json'),
            path.join(options.outputRoot, 'controller-result.json'),
        ]) {
            if (fs.existsSync(file)) {
                snapshotText(file, path.join(checkpointDirectory, path.basename(file)));
            }
        }
        fs.writeFileSync(path.join(checkpointDirectory, 'checkpoint.json'), JSON.stringify({
            reason, sequence, capturedAt: new Date().toISOString(),
            sourceSha: options.sourceSha, workflowSha: options.workflowSha,
            runnerIndex: options.runnerIndex, runAttempt: options.runAttempt,
            runnerPid: runner?.pid, observerPid: observer?.pid,
            runnerExitCode: result.runnerExitCode, snapshotErrors,
        }, undefined, 2));
        if (snapshotErrors.length) {
            const message = `Checkpoint ${sequence} could not snapshot ${snapshotErrors.length} files.`;
            result.diagnosticErrors.push(message);
            console.warn(message);
        }
        // Invoke the exact entry point declared in the already-pinned first-party
        // action.yml. This reuses its shipped SDK; no npm feed change or bespoke
        // artifact protocol is needed. Immutable names preserve earlier evidence.
        const artifactName = `tree-actions-live-r${options.runnerIndex}-a${options.runAttempt}-c${String(sequence).padStart(3, '0')}`;
        const upload = spawn(process.execPath, [options.uploadActionPath], {
            env: {
                ...process.env,
                INPUT_NAME: artifactName,
                INPUT_PATH: checkpointDirectory,
                'INPUT_IF-NO-FILES-FOUND': 'error',
                'INPUT_RETENTION-DAYS': '14',
                'INPUT_COMPRESSION-LEVEL': '6',
                INPUT_OVERWRITE: 'false',
                'INPUT_INCLUDE-HIDDEN-FILES': 'true',
                INPUT_ARCHIVE: 'true',
                GITHUB_OUTPUT: path.join(options.outputRoot, `upload-${sequence}.txt`),
            },
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        const finishUploadLog = pipeLog(upload, path.join(options.outputRoot, 'uploads.log'), true);
        let code: number | null;
        let timeout: NodeJS.Timeout | undefined;
        try {
            code = await Promise.race([
                childExit(upload),
                new Promise<never>((_, reject) => {
                    timeout = setTimeout(() => {
                        upload.kill();
                        reject(new Error(`Remote checkpoint ${sequence} exceeded its upload deadline.`));
                    }, 120000);
                }),
            ]);
        } finally {
            clearTimeout(timeout);
            finishUploadLog();
        }
        if (code !== 0) {
            throw new Error(`Remote checkpoint ${sequence} failed with exit ${code}.`);
        }
        result.checkpointCount++;
        console.log(`Remote checkpoint ${artifactName} persisted (${reason}).`);
    };

    try {
        // Prove remote persistence before launching any test or target process.
        await checkpoint('before-workload');
        const childEnv = workloadEnvironment(process.env);
        observer = spawn('pwsh', [
            '-NoLogo', '-NoProfile', '-File',
            path.join(options.toolsRoot, 'Observe-TreeActionsProcesses.ps1'),
            '-OutputDirectory', observations,
            '-DcpDirectory', process.env.ASPIRE_DCP_PATH ?? '',
            '-StopFile', observerStop,
            '-MaximumSeconds', '1200',
        ], { env: childEnv, stdio: ['ignore', 'pipe', 'pipe'] });
        finishObserverLog = pipeLog(observer, observerLog, false);
        observerFinished = childExit(observer);
        // Keep process-exit failures observable even while checkpointing.
        let observerFailure: Error | undefined;
        observerFinished.catch(error => { observerFailure = error instanceof Error ? error : new Error(String(error)); });
        const statusFile = path.join(observations, 'observer-status.json');
        const observerDeadline = Date.now() + 30000;
        while (true) {
            if (observerFailure || observer.exitCode !== null || Date.now() > observerDeadline) {
                throw observerFailure ?? new Error('Process observer failed to become ready.');
            }
            if (fs.existsSync(statusFile)) {
                const status: unknown = JSON.parse(fs.readFileSync(statusFile, 'utf8').replace(/^\uFEFF/, ''));
                if (observerReady(status)) {
                    break;
                }
            }
            await new Promise(resolve => setTimeout(resolve, 200));
        }
        runner = spawn(options.nodePath, ['scripts/run-e2e.js'], {
            cwd: options.extensionRoot,
            env: {
                ...childEnv,
                ASPIRE_EXTENSION_E2E_SHARD: shard,
                ASPIRE_EXTENSION_E2E_SPEC: 'out/test-e2e/test-e2e/treeActionsRestartDiagnostics.e2e.test.js',
                ASPIRE_EXTENSION_E2E_TEMP_ROOT: options.temporaryRoot,
                ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS: '900000',
                TREE_ACTIONS_DIAGNOSTICS_DIRECTORY: observations,
                TREE_ACTIONS_DIAGNOSTIC_CYCLES: '30',
                DCP_DIAGNOSTICS_LOG_LEVEL: 'debug',
                DCP_DIAGNOSTICS_LOG_FOLDER: path.join(resultsDirectory, 'native-dcp'),
            },
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        finishRunnerLog = pipeLog(runner, runnerLog, true);
        runnerFinished = childExit(runner).then(code => {
            result.runnerExitCode = code;
            runnerDone = true;
            return code;
        });
        let runnerFailure: Error | undefined;
        runnerFinished.catch(error => {
            runnerFailure = error instanceof Error ? error : new Error(String(error));
            runnerDone = true;
        });
        let lastCheckpoint = Date.now();
        let observedTraceLength = 0;
        let lastMilestone = '';
        const deadline = Date.now() + 18 * 60 * 1000;
        while (!runnerDone) {
            if (observerFailure || observer.exitCode !== null) {
                throw observerFailure ?? new Error(`Process observer exited prematurely: ${observer.exitCode}.`);
            }
            if (Date.now() > deadline) {
                throw new Error('Diagnostic controller deadline exceeded.');
            }
            await new Promise(resolve => setTimeout(resolve, 1000));
            const traceFile = path.join(observations, 'lifecycle.jsonl');
            let milestone = '';
            if (fs.existsSync(traceFile)) {
                const trace = fs.readFileSync(traceFile, 'utf8');
                const added = trace.slice(observedTraceLength);
                observedTraceLength = trace.length;
                // Each flushed JSONL record is {"phase":"cycle-complete",...} or
                // {"phase":"command-error",...}; a trailing partial record waits
                // for the next periodic snapshot instead of being parsed as JSON.
                const records = added.split('\n').filter(line => /"phase":"(?:cycle-complete|command-error|suite-failed)"/.test(line));
                milestone = records.at(-1) ?? '';
            }
            if ((milestone && milestone !== lastMilestone) || Date.now() - lastCheckpoint >= 15000) {
                await checkpoint(milestone ? 'lifecycle-milestone' : 'heartbeat');
                lastCheckpoint = Date.now();
                lastMilestone = milestone || lastMilestone;
            }
        }
        if (runnerFailure) {
            throw runnerFailure;
        }
        await runnerFinished;
    } catch (error) {
        const message = error instanceof Error ? redact(error.message) : 'Unknown diagnostic controller failure';
        result.diagnosticErrors.push(message);
        console.error(message);
        fs.writeFileSync(path.join(observations, 'abort'), message);
    } finally {
        if (runner && !runnerDone) {
            // The stress test sees the abort marker at its next cycle boundary
            // and runs normal teardown. Give that cleanup a chance before
            // forcibly terminating only this controller's owned process tree.
            await Promise.race([
                runnerFinished?.catch(() => undefined),
                new Promise(resolve => { setTimeout(resolve, 120000).unref(); }),
            ]);
            if (!runnerDone && runner.pid) {
                result.diagnosticErrors.push('Forced termination of the owned E2E process tree after abort grace period.');
                const cleanup = spawn('taskkill', ['/pid', String(runner.pid), '/t', '/f'], {
                    stdio: ['ignore', 'pipe', 'pipe'],
                    env: workloadEnvironment(process.env),
                });
                const finishCleanupLog = pipeLog(cleanup, path.join(options.outputRoot, 'cleanup.log'), false);
                const cleanupCode = await childExit(cleanup);
                finishCleanupLog();
                if (cleanupCode !== 0) {
                    result.diagnosticErrors.push(`Owned process-tree termination returned ${cleanupCode}.`);
                }
            }
        }
        finishRunnerLog?.();
        fs.writeFileSync(observerStop, 'stop');
        if (observerFinished) {
            try {
                const observerExit = await Promise.race([
                    observerFinished,
                    new Promise<undefined>(resolve => { setTimeout(() => resolve(undefined), 15000).unref(); }),
                ]);
                if (observerExit === undefined) {
                    result.diagnosticErrors.push('Process observer did not stop after its sentinel.');
                    observer?.kill();
                } else {
                    result.observerExitCode = observerExit;
                    const status: unknown = JSON.parse(fs.readFileSync(path.join(observations, 'observer-status.json'), 'utf8').replace(/^\uFEFF/, ''));
                    if (status === null || typeof status !== 'object' || !('state' in status) || status.state !== 'stopped') {
                        result.diagnosticErrors.push('Observer did not report a clean sentinel stop.');
                    }
                }
            } catch (error) {
                result.diagnosticErrors.push(error instanceof Error ? redact(error.message) : 'Observer termination failed');
            }
        }
        finishObserverLog?.();
        fs.writeFileSync(path.join(options.outputRoot, 'controller-result.json'), JSON.stringify(result, undefined, 2));
        try {
            await checkpoint('final');
        } catch (error) {
            result.diagnosticErrors.push(error instanceof Error ? redact(error.message) : 'Final checkpoint failed');
        }
        fs.writeFileSync(path.join(options.outputRoot, 'controller-result.json'), JSON.stringify(result, undefined, 2));
    }
    return result;
}
