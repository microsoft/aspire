'use strict';

const { spawnSync } = require('child_process');

const PROCESS_LISTING_TIMEOUT_MS = 15000;
const ORPHAN_TERMINATION_GRACE_MS = 2000;
const ORPHAN_TERMINATION_CONFIRMATION_TIMEOUT_MS = 5000;
const ORPHAN_TERMINATION_POLL_INTERVAL_MS = 250;
const NON_RETRYABLE_ERROR_FLAG = Symbol('aspireNonRetryableError');

/**
 * Retries `execute` until it succeeds, the attempts run out, or it fails in a way that makes
 * another attempt unsafe.
 *
 * `beforeRetry` exists to wipe partial state between attempts, so it can only run when the state
 * is known. A non-retryable failure means it is not: wiping and rebuilding on top of it is the
 * hazard rather than the remedy.
 */
function runWithRetries(execute, { attempts, retryDelayMs, beforeRetry, description }) {
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt++) {
    try {
      execute();
      return;
    }
    catch (error) {
      lastError = error;
      if (attempt === attempts || isNonRetryableError(error)) {
        break;
      }

      console.warn(`${description} failed on attempt ${attempt}/${attempts}: ${error instanceof Error ? error.message : String(error)}`);
      beforeRetry?.();
      sleepSynchronously(retryDelayMs);
    }
  }

  throw lastError;
}

/**
 * Kills processes still working inside a directory after the process the runner started has been
 * killed for exceeding its timeout, and throws unless it can prove none are left.
 *
 * Node reports a `spawnSync` timeout as `ETIMEDOUT` after signalling only the process it started.
 * ExTester unpacks zips on macOS and Linux by shelling out (`exec` runs `/bin/sh -c 'unzip ...'`),
 * so those two processes are reparented and keep extracting into a directory that is about to be
 * validated and published as an immutable cache entry.
 *
 * They are matched by the storage path in their command line rather than by process group, because
 * putting the child in its own group would take it out of the terminal's foreground group and stop
 * Ctrl-C from reaching a download. The path is a `mkdtemp` name unique to the run, so the match
 * cannot pick up an unrelated process. Windows unpacks in-process and leaves nothing behind.
 *
 * "Probably clean" is not good enough here: the caller decides whether the staging directory is
 * safe to wipe and reuse, so anything short of an empty match after SIGKILL is raised rather than
 * logged and swallowed.
 */
function terminateOrphanedDescendants(storagePath) {
  if (process.platform === 'win32') {
    return;
  }

  const orphanPids = listProcessesWorkingUnder(storagePath);
  if (orphanPids.length === 0) {
    return;
  }

  console.warn(`Terminating ${orphanPids.length} process(es) still writing to ${storagePath} after a setup timeout.`);
  signalProcesses(orphanPids, 'SIGTERM');
  sleepSynchronously(ORPHAN_TERMINATION_GRACE_MS);

  const confirmationDeadline = Date.now() + ORPHAN_TERMINATION_CONFIRMATION_TIMEOUT_MS;
  let escalated = false;
  for (;;) {
    // Re-match by path on every pass instead of reusing the first list. A process that has already
    // exited must not be signalled again, because the kernel can have handed its pid to something
    // unrelated by then.
    const remainingPids = listProcessesWorkingUnder(storagePath);
    if (remainingPids.length === 0) {
      return;
    }

    if (!escalated) {
      signalProcesses(remainingPids, 'SIGKILL');
      escalated = true;
    } else if (Date.now() >= confirmationDeadline) {
      throw new Error(`process(es) ${remainingPids.join(', ')} are still writing to ${storagePath} after SIGKILL.`);
    }

    sleepSynchronously(ORPHAN_TERMINATION_POLL_INTERVAL_MS);
  }
}

/**
 * Returns the pids of processes whose command line mentions `storagePath`, excluding this process
 * and whatever started it.
 *
 * `ps -Awwo pid=,args=` prints one process per line with no header and untruncated arguments:
 *   57231 unzip -qo /var/folders/f9/T/aev-Xa1/cache-staging/1.122.1-stable.zip
 *
 * Every way `ps` can fail is raised rather than reported as "nothing found". An empty list is the
 * answer that lets the caller reuse the directory, and claiming it when the process table was
 * never read is how a live `unzip` ends up inside a published cache entry.
 */
function listProcessesWorkingUnder(storagePath) {
  const listing = spawnSync('ps', ['-Awwo', 'pid=,args='], { encoding: 'utf8', timeout: PROCESS_LISTING_TIMEOUT_MS });
  if (listing.error) {
    throw new Error(`'ps' could not be run: ${listing.error.message}`);
  }

  if (listing.signal) {
    throw new Error(`'ps' was terminated by ${listing.signal}.`);
  }

  if (listing.status !== 0) {
    throw new Error(`'ps' exited with code ${listing.status}: ${String(listing.stderr ?? '').trim() || 'no diagnostics'}`);
  }

  if (typeof listing.stdout !== 'string') {
    throw new Error(`'ps' produced no output.`);
  }

  const pids = [];
  for (const line of listing.stdout.split('\n')) {
    const match = /^\s*(\d+)\s+(.+)$/.exec(line);
    if (!match) {
      continue;
    }

    const pid = Number(match[1]);
    if (pid === process.pid || pid === process.ppid || !match[2].includes(storagePath)) {
      continue;
    }

    pids.push(pid);
  }

  return pids;
}

function signalProcesses(pids, signal) {
  for (const pid of pids) {
    try {
      process.kill(pid, signal);
    } catch {
      // ESRCH is the expected outcome once the process has exited. Whether the signal landed is
      // not decided here: `terminateOrphanedDescendants` re-reads the process table instead.
    }
  }
}

function markErrorNonRetryable(error) {
  error[NON_RETRYABLE_ERROR_FLAG] = true;
  return error;
}

function isNonRetryableError(error) {
  return Boolean(error) && error[NON_RETRYABLE_ERROR_FLAG] === true;
}

function sleepSynchronously(milliseconds) {
  const buffer = new SharedArrayBuffer(4);
  Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}

module.exports = {
  isNonRetryableError,
  listProcessesWorkingUnder,
  markErrorNonRetryable,
  runWithRetries,
  sleepSynchronously,
  terminateOrphanedDescendants,
};
