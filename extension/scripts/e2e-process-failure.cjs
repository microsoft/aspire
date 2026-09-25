'use strict';

const { hasCompletedMochaTestFailures } = require('./e2e-mocha-results.cjs');

const VALID_REASONS = new Set(['exit-code', 'timeout', 'signal', 'spawn']);

class E2eProcessError extends Error {
  constructor(reason, command, args, options = {}) {
    if (!VALID_REASONS.has(reason)) {
      throw new TypeError(`Unsupported E2E process failure reason '${reason}'.`);
    }

    const {
      exitCode = null,
      signal = null,
      timeout = null,
      didNotExit = false,
      diagnosticsSuffix = '',
      cause,
    } = options;
    super(createMessage(reason, command, args, exitCode, signal, timeout, didNotExit, diagnosticsSuffix, cause), cause === undefined ? undefined : { cause });
    this.name = 'E2eProcessError';
    this.reason = reason;
    this.command = command;
    this.args = [...args];
    this.exitCode = exitCode;
    this.signal = signal;
    this.timeout = timeout;
    this.didNotExit = didNotExit;
    this.diagnosticsSuffix = diagnosticsSuffix;
  }
}

function shouldAllowAdvisoryTestFailure(error, results, cleanupFailed) {
  // ExTester's suite runner normalizes completed Mocha failures to exit code 1. Any other
  // numeric status can represent a later Node/runtime failure and must remain blocking.
  return error instanceof E2eProcessError
    && error.reason === 'exit-code'
    && error.exitCode === 1
    && hasCompletedMochaTestFailures(results)
    && !cleanupFailed;
}

function createMessage(reason, command, args, exitCode, signal, timeout, didNotExit, diagnosticsSuffix, cause) {
  const commandLine = formatCommand(command, args);
  switch (reason) {
    case 'exit-code':
      return `${commandLine} exited with code ${exitCode ?? 'unknown'}.${diagnosticsSuffix}`;
    case 'timeout':
      return `${commandLine} timed out${timeout === null ? '' : ` after ${timeout}ms`}${didNotExit ? ' and did not exit after process-tree termination' : ''}.${diagnosticsSuffix}`;
    case 'signal':
      return `${commandLine} exited due to signal ${signal ?? 'unknown'}.${diagnosticsSuffix}`;
    case 'spawn':
      return `Failed to start ${commandLine}: ${cause instanceof Error ? cause.message : String(cause ?? 'unknown error')}.${diagnosticsSuffix}`;
    default:
      throw new TypeError(`Unsupported E2E process failure reason '${reason}'.`);
  }
}

function formatCommand(command, args) {
  return [command, ...args]
    .map(segment => /\s/.test(segment) ? JSON.stringify(segment) : segment)
    .join(' ');
}

module.exports = {
  E2eProcessError,
  shouldAllowAdvisoryTestFailure,
};
