import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { execFileSync } from 'child_process';

interface DownloadRetryModule {
    isNonRetryableError(error: unknown): boolean;
    listProcessesWorkingUnder(storagePath: string): number[];
    markErrorNonRetryable<TError extends Error>(error: TError): TError;
    runWithRetries(execute: () => void, options: {
        attempts: number;
        retryDelayMs: number;
        beforeRetry?: () => void;
        description: string;
    }): void;
    sleepSynchronously(milliseconds: number): void;
    terminateOrphanedDescendants(storagePath: string): void;
}

const extensionRoot = path.resolve(__dirname, '..', '..');
const retry = require(path.join(extensionRoot, 'scripts', 'e2e-download-retry.js')) as DownloadRetryModule;

const isPosix = process.platform !== 'win32';
const createdRoots: string[] = [];
const orphanPidsToClean: number[] = [];
let originalPath: string | undefined;

function createTestRoot(name: string): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), `aspire-e2e-retry-${name}-`));
    createdRoots.push(root);
    return root;
}

/**
 * Starts a writer that is not a child of this test process.
 *
 * The double fork matters. A direct child that is killed becomes a zombie until this process reaps
 * it, and `terminateOrphanedDescendants` blocks the event loop while it waits, so the zombie would
 * still be listed by `ps` and the confirmation would fail for reasons the runner never sees. In
 * production the unpack processes are already orphans -- their parent was killed by `spawnSync`'s
 * timeout -- so `init` reaps them. Reparenting here reproduces that.
 */
function startOrphanedWriter(storagePath: string): number {
    const logPath = path.join(storagePath, 'writer.log');
    const pidPath = path.join(storagePath, 'writer.pid');
    const script = `while :; do printf x >> "${logPath}"; sleep 0.05; done`;

    execFileSync('/bin/sh', ['-c', `${script} >/dev/null 2>&1 & echo $! > "${pidPath}"`], {
        stdio: 'ignore',
    });

    const pid = Number(fs.readFileSync(pidPath, 'utf8').trim());
    assert.ok(Number.isInteger(pid) && pid > 0, 'the writer must report a pid');
    orphanPidsToClean.push(pid);
    return pid;
}

/**
 * A process id the kernel can never hand out, so a fixture can stand in for a process without ever
 * naming a real one.
 *
 * Probing for a free pid and then using it is a race: nothing reserves it, and the pid can be
 * recycled between the probe and the signal. This value cannot be, because it is above every
 * ceiling the two platforms allow - Linux caps `/proc/sys/kernel/pid_max` at 2^22 and macOS at
 * 99998 - while still fitting the int32 `process.kill` requires. Signalling it always fails with
 * ESRCH.
 */
const UNALLOCATABLE_PROCESS_ID = 2147483646;

function waitForFile(filePath: string, timeoutMs = 10000): void {    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (fs.existsSync(filePath) && fs.statSync(filePath).size > 0) {
            return;
        }

        retry.sleepSynchronously(50);
    }

    throw new Error(`Timed out waiting for ${filePath} to be written.`);
}

/**
 * Puts a stub `ps` on PATH so the failure branches can be exercised without breaking the machine.
 */
function stubProcessListing(root: string, body: string | null): void {
    const binDirectory = path.join(root, 'stub-bin');
    fs.mkdirSync(binDirectory, { recursive: true });

    if (body !== null) {
        const stubPath = path.join(binDirectory, 'ps');
        fs.writeFileSync(stubPath, body);
        fs.chmodSync(stubPath, 0o755);
    }

    originalPath = originalPath ?? process.env.PATH;
    process.env.PATH = binDirectory;
}

suite('E2E download retry and orphan cleanup', () => {
    teardown(() => {
        if (originalPath !== undefined) {
            process.env.PATH = originalPath;
            originalPath = undefined;
        }

        while (orphanPidsToClean.length > 0) {
            const pid = orphanPidsToClean.pop()!;
            try {
                process.kill(pid, 'SIGKILL');
            } catch {
                // Already gone, which is what most of these tests assert.
            }
        }

        while (createdRoots.length > 0) {
            fs.rmSync(createdRoots.pop()!, { recursive: true, force: true });
        }
    });

    test('retries until success and cleans between attempts', () => {
        const observed: string[] = [];
        let attempts = 0;

        retry.runWithRetries(() => {
            attempts++;
            observed.push(`attempt-${attempts}`);
            if (attempts < 3) {
                throw new Error('transient');
            }
        }, {
            attempts: 5,
            retryDelayMs: 1,
            beforeRetry: () => observed.push('clean'),
            description: 'flaky command',
        });

        assert.deepStrictEqual(observed, ['attempt-1', 'clean', 'attempt-2', 'clean', 'attempt-3']);
    });

    test('stops immediately and skips cleanup when a failure is non-retryable', () => {
        const observed: string[] = [];
        let attempts = 0;

        // `beforeRetry` wipes partial downloads. Running it while an unpack process may still be
        // writing, then letting a later attempt validate and publish the result, is the corruption
        // the non-retryable path exists to avoid.
        assert.throws(() => retry.runWithRetries(() => {
            attempts++;
            observed.push(`attempt-${attempts}`);
            throw retry.markErrorNonRetryable(new Error('orphans unaccounted for'));
        }, {
            attempts: 5,
            retryDelayMs: 1,
            beforeRetry: () => observed.push('clean'),
            description: 'timed out command',
        }), /orphans unaccounted for/);

        assert.strictEqual(attempts, 1);
        assert.deepStrictEqual(observed, ['attempt-1']);
    });

    test('marks and recognises non-retryable errors without affecting ordinary ones', () => {
        assert.strictEqual(retry.isNonRetryableError(new Error('ordinary')), false);
        assert.strictEqual(retry.isNonRetryableError(retry.markErrorNonRetryable(new Error('fatal'))), true);
        assert.strictEqual(retry.isNonRetryableError(undefined), false);
        assert.strictEqual(retry.isNonRetryableError(null), false);
    });

    (isPosix ? test : test.skip)('finds a live process working under the storage path and ignores this one', () => {
        const root = createTestRoot('listing');
        const pid = startOrphanedWriter(root);
        waitForFile(path.join(root, 'writer.log'));

        const found = retry.listProcessesWorkingUnder(root);

        assert.ok(found.includes(pid), `expected ${pid} in ${JSON.stringify(found)}`);
        assert.ok(!found.includes(process.pid));
        assert.deepStrictEqual(retry.listProcessesWorkingUnder(path.join(root, 'no-such-subdirectory')), []);
    });

    (isPosix ? test : test.skip)('kills an orphaned writer and confirms it stopped writing', () => {
        const root = createTestRoot('terminate');
        const logPath = path.join(root, 'writer.log');
        startOrphanedWriter(root);
        waitForFile(logPath);

        retry.terminateOrphanedDescendants(root);

        assert.deepStrictEqual(retry.listProcessesWorkingUnder(root), []);

        const sizeAfterTermination = fs.statSync(logPath).size;
        retry.sleepSynchronously(500);
        assert.strictEqual(fs.statSync(logPath).size, sizeAfterTermination, 'the writer must not still be appending');
    });

    (isPosix ? test : test.skip)('returns without signalling anything when the storage path is quiet', () => {
        const root = createTestRoot('quiet');

        retry.terminateOrphanedDescendants(root);

        assert.deepStrictEqual(retry.listProcessesWorkingUnder(root), []);
    });

    (isPosix ? test : test.skip)('raises rather than reporting an empty listing when ps cannot be run', () => {
        const root = createTestRoot('missing-ps');
        stubProcessListing(root, null);

        assert.throws(() => retry.listProcessesWorkingUnder(root), /'ps' could not be run/);
        assert.throws(() => retry.terminateOrphanedDescendants(root), /'ps' could not be run/);
    });

    (isPosix ? test : test.skip)('raises rather than reporting an empty listing when ps exits nonzero', () => {
        const root = createTestRoot('failing-ps');
        stubProcessListing(root, '#!/bin/sh\necho "ps: permission denied" 1>&2\nexit 1\n');

        assert.throws(() => retry.listProcessesWorkingUnder(root), /'ps' exited with code 1: ps: permission denied/);
        assert.throws(() => retry.terminateOrphanedDescendants(root), /'ps' exited with code 1/);
    });

    (isPosix ? test : test.skip)('raises when ps succeeds but the orphan survives SIGKILL', () => {
        const root = createTestRoot('unkillable');
        // A stub that keeps reporting a pid nothing owns stands in for a process that cannot be
        // killed, which is otherwise unreachable in a test: no process survives SIGKILL. The pid
        // is one the kernel can never allocate, so the SIGTERM and SIGKILL this drives fail with
        // ESRCH and cannot reach anything else on the machine. Probing for a merely unused pid
        // would leave a window for it to be recycled before the signal lands, and naming a real
        // one (1, for instance) would kill it outright on any runner that happens to be root.
        stubProcessListing(root, `#!/bin/sh\necho "    ${UNALLOCATABLE_PROCESS_ID} unzip -qo ${root}/vscode.zip"\n`);

        assert.throws(() => retry.terminateOrphanedDescendants(root), /are still writing to .* after SIGKILL/);
    });
});
