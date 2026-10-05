'use strict';

const { E2eProcessError } = require('./e2e-process-failure.cjs');

const DEFAULT_FORCE_TIMEOUT = 15000;

function runWithProcessTreeTimeout(command, args, options) {
  const {
    diagnosticsSuffix = '',
    forceTimeout = DEFAULT_FORCE_TIMEOUT,
    quoteShellArgument = value => value,
    spawn,
    spawnOptions = {},
    terminateProcessTree,
    timeout,
    useShell = false,
  } = options;

  if (typeof terminateProcessTree !== 'function') {
    return Promise.reject(new TypeError('terminateProcessTree must be a function.'));
  }

  return new Promise((resolve, reject) => {
    const child = useShell
      ? spawn([command, ...args].map(quoteShellArgument).join(' '), [], {
        ...spawnOptions,
        shell: true,
      })
      : spawn(command, args, {
        ...spawnOptions,
        shell: false,
      });

    let timedOut = false;
    let settled = false;
    let forceTimer;
    const timeoutTimer = setTimeout(() => {
      timedOut = true;
      terminateProcessTree(child.pid, 'SIGTERM');
      forceTimer = setTimeout(() => {
        if (settled) {
          return;
        }

        terminateProcessTree(child.pid, 'SIGKILL');
        child.removeAllListeners();
        child.unref();
        settle();
        reject(new E2eProcessError('timeout', command, args, {
          timeout,
          didNotExit: true,
          diagnosticsSuffix,
        }));
      }, forceTimeout);
    }, timeout);

    child.on('error', error => {
      if (settled) {
        return;
      }

      settle();
      reject(new E2eProcessError('spawn', command, args, {
        cause: error,
        diagnosticsSuffix,
      }));
    });

    child.on('close', (exitCode, signal) => {
      if (settled) {
        return;
      }

      settle();
      if (timedOut) {
        reject(new E2eProcessError('timeout', command, args, {
          timeout,
          diagnosticsSuffix,
        }));
        return;
      }

      if (typeof exitCode !== 'number') {
        reject(new E2eProcessError('signal', command, args, {
          signal,
          diagnosticsSuffix,
        }));
        return;
      }

      if (exitCode !== 0) {
        reject(new E2eProcessError('exit-code', command, args, {
          exitCode,
          diagnosticsSuffix,
        }));
        return;
      }

      resolve();
    });

    function settle() {
      settled = true;
      clearTimeout(timeoutTimer);
      if (forceTimer) {
        clearTimeout(forceTimer);
      }
    }
  });
}

module.exports = {
  DEFAULT_FORCE_TIMEOUT,
  runWithProcessTreeTimeout,
};
