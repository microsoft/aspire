'use strict';

// Temporary passive evidence for https://github.com/microsoft/aspire/issues/20184.
// This helper neither changes profiles nor controls browser/debugger processes.
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function startBlazorProfileDiagnostics({ runRoot, storageDir, resultsDir, controlFile, stateFile }) {
  const output = path.join(resultsDir, 'blazor-profile-observations.jsonl');
  const reportedErrors = new Set();
  const relativeOwnedPath = file => {
    const relative = path.relative(runRoot, file);
    if (path.isAbsolute(relative) || relative === '..' || relative.startsWith(`..${path.sep}`)) {
      throw Object.assign(new Error('Diagnostic path is outside the owned run root.'), { code: 'OUTSIDE_RUN_ROOT' });
    }
    return relative;
  };
  relativeOwnedPath(storageDir);

  function optionalRead(action) {
    try {
      return action();
    }
    catch (error) {
      // A process or profile can disappear between enumeration and inspection.
      if (error.code === 'ENOENT' || error.code === 'ESRCH') {
        return undefined;
      }
      throw error;
    }
  }

  function readProcesses() {
    const result = spawnSync('ps', ['-eo', 'pid=,ppid=,comm=,args='], { encoding: 'utf8', timeout: 5000 });
    if (result.status !== 0 || result.error) {
      throw Object.assign(new Error('Process observation failed.'), { code: result.error?.code ?? 'PS_FAILED' });
    }
    const processes = new Map();
    for (const line of result.stdout.split('\n')) {
      // ps emits "<pid> <ppid> <comm> <args>"; args can contain spaces and secrets.
      // Raw args are used only for ownership matching, never included in the output.
      const match = line.match(/^\s*(\d+)\s+(\d+)\s+(\S+)\s*(.*)$/);
      if (!match) {
        continue;
      }
      const pid = Number(match[1]);
      processes.set(pid, {
        referencesRunRoot: match[4].includes(`${runRoot}${path.sep}`),
        summary: { pid, ppid: Number(match[2]), executable: path.basename(match[4].split(' ')[0]) },
      });
    }
    return processes;
  }

  function readLocks(directory, processes, owned, locks) {
    // Check every ancestor without following symlinks, including the starting directory.
    // VS Code/Chrome download-cache projections must never be traversed by diagnostics.
    const relative = relativeOwnedPath(directory);
    let ancestor = runRoot;
    for (const segment of relative.split(path.sep).filter(Boolean)) {
      ancestor = path.join(ancestor, segment);
      const stat = optionalRead(() => fs.lstatSync(ancestor));
      if (!stat || !stat.isDirectory() || stat.isSymbolicLink()) {
        return;
      }
    }
    const entries = optionalRead(() => fs.readdirSync(directory, { withFileTypes: true })) ?? [];
    for (const entry of entries) {
      const file = path.join(directory, entry.name);
      if (['SingletonLock', 'SingletonCookie', 'SingletonSocket', 'code.lock'].includes(entry.name)) {
        const stat = optionalRead(() => fs.lstatSync(file));
        if (!stat) {
          continue;
        }
        let targetPid;
        let codeLockFormat;
        if (entry.name === 'SingletonLock' && stat.isSymbolicLink()) {
          // Linux Chromium writes SingletonLock -> "<hostname>-<pid>", including when
          // the owner is gone. lstat/readlink preserve that distinction without following it.
          const target = optionalRead(() => fs.readlinkSync(file));
          targetPid = Number(target?.match(/-(\d+)$/)?.[1]) || undefined;
        }
        if (entry.name === 'code.lock' && stat.isFile() && stat.size <= 4096) {
          // js-debug's code.lock carries a PID. Retain only a numeric PID from JSON
          // {"pid":1234} or a bare number; never persist other fields or raw contents.
          const text = optionalRead(() => fs.readFileSync(file, 'utf8'));
          if (text !== undefined) {
            try {
              const value = JSON.parse(text);
              const pid = typeof value === 'number' ? value : value?.pid;
              if (Number.isSafeInteger(pid) && pid > 0) {
                targetPid = pid;
                codeLockFormat = 'pid';
              } else {
                codeLockFormat = 'unrecognized';
              }
            }
            catch (error) {
              if (!(error instanceof SyntaxError)) {
                throw error;
              }
              codeLockFormat = 'unrecognized';
            }
          }
        }
        locks.push({
          path: relativeOwnedPath(file),
          symbolicLink: stat.isSymbolicLink(),
          size: stat.size,
          modifiedAt: stat.mtime.toISOString(),
          targetPid,
          targetAlive: targetPid ? processes.has(targetPid) : undefined,
          targetOwned: targetPid ? owned.has(targetPid) : undefined,
          targetExecutable: targetPid ? processes.get(targetPid)?.summary.executable : undefined,
          codeLockFormat,
        });
      }
      // Read profile lock entries, but not browser databases, cookie values, or cache trees.
      if (entry.isDirectory() && path.basename(directory) !== '.profile') {
        readLocks(file, processes, owned, locks);
      }
    }
  }

  function capture(phase) {
    const timestamp = new Date().toISOString();
    try {
      const processes = readProcesses();
      const owned = new Set([process.pid]);
      let changed;
      do {
        changed = false;
        for (const [pid, item] of processes) {
          if (!owned.has(pid) && (owned.has(item.summary.ppid) || item.referencesRunRoot)) {
            owned.add(pid);
            changed = true;
          }
        }
      } while (changed);
      const locks = [];
      readLocks(path.join(storageDir, 'settings', 'User', 'workspaceStorage'), processes, owned, locks);
      readLocks(path.join(storageDir, 'settings', 'User', 'globalStorage', 'ms-vscode.js-debug'), processes, owned, locks);

      // These two known runner-owned files can contain secrets. Project only the
      // revision/status and the three fixed scenario names, not arbitrary payloads.
      const controlText = optionalRead(() => fs.readFileSync(controlFile, 'utf8'));
      const stateText = optionalRead(() => fs.readFileSync(stateFile, 'utf8'));
      const control = controlText === undefined ? undefined : JSON.parse(controlText);
      const state = stateText === undefined ? undefined : JSON.parse(stateText);
      const resource = control?.command?.resourceName;
      const revision = Number.isSafeInteger(control?.revision) ? control.revision : undefined;
      const controlStatus = state?.control?.revision === revision ? state?.control?.status : undefined;
      fs.appendFileSync(output, JSON.stringify({
        timestamp, phase, revision,
        scenario: ['standalone', 'hosted-global', 'hosted-per-page'].includes(resource) ? resource : undefined,
        controlStatus: ['started', 'applied', 'error'].includes(controlStatus) ? controlStatus : undefined,
        locks,
        processes: [...owned].map(pid => processes.get(pid)?.summary).filter(Boolean),
      }) + '\n');
    }
    catch (error) {
      const code = typeof error.code === 'string' ? error.code : error.name;
      // Diagnostic failure must be explicit without changing the baseline test result
      // or printing potentially sensitive exception messages/command lines.
      if (!reportedErrors.has(code)) {
        reportedErrors.add(code);
        console.warn(`Blazor profile observation failed (${code}).`);
      }
      try {
        fs.appendFileSync(output, JSON.stringify({ timestamp, phase, observationError: code }) + '\n');
      }
      catch (writeError) {
        console.warn(`Unable to persist Blazor profile observation (${writeError.code ?? writeError.name}).`);
      }
    }
  }

  const timer = setInterval(() => capture('periodic'), 3000);
  timer.unref();
  capture('start');
  return {
    capture,
    stop() {
      clearInterval(timer);
      capture('before-temporary-root-cleanup');
    },
  };
}

module.exports = { startBlazorProfileDiagnostics };
