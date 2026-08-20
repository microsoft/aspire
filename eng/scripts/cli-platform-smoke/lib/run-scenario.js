'use strict';

const path = require('path');

const { buildAspireCommand } = require('./aspire-command');
const { ShellSession } = require('./shell-session');

const defaultTimeoutMs = 600_000;
const cleanupTimeoutMs = 130_000;
const callbackSettlementTimeoutMs = 5_000;

async function runScenario(scenario, context) {
  validateScenario(scenario);

  const timeoutMs = scenario.timeoutMs || defaultTimeoutMs;
  const artifactCounts = new Map();
  const controller = createShellController(context, scenario.description, timeoutMs, artifactCounts);
  const execution = runCallback(scenario.callback, controller);
  let failure = null;

  try {
    await withTimeout(execution, timeoutMs);
  } catch (error) {
    failure = error;
    await controller.dispose();
    await waitForSettlement(execution, callbackSettlementTimeoutMs);
  } finally {
    await controller.dispose();
  }

  if (scenario.cleanup) {
    const cleanupController = createShellController(
      context,
      `${scenario.description} cleanup`,
      cleanupTimeoutMs,
      artifactCounts);

    try {
      await withTimeout(runCallback(scenario.cleanup, cleanupController), cleanupTimeoutMs);
    } catch (error) {
      if (!failure) {
        failure = error;
      }
    } finally {
      await cleanupController.dispose();
    }
  }

  if (failure) {
    throw new Error(`${scenario.description}: ${failure.message}`, { cause: failure });
  }
}

function createShellController(context, description, timeoutMs, artifactCounts) {
  let activeRun = null;
  let disposed = false;
  let session = null;
  let sessionCwd = null;

  async function completeActiveRun() {
    if (!activeRun) {
      return;
    }

    const run = activeRun;
    try {
      await run.waitForExit(timeoutMs);
    } catch (error) {
      run.flushArtifacts();
      throw error;
    } finally {
      activeRun = null;
    }
  }

  async function runAspireCommand(args, options = {}) {
    ensureNotDisposed();
    await completeActiveRun();

    const cwd = options.cwd || context.projectRoot;
    if (!session || sessionCwd !== cwd) {
      await session?.dispose();
      session = null;
      sessionCwd = null;
      ensureNotDisposed();

      const startedSession = await ShellSession.start(cwd);
      if (disposed) {
        await startedSession.dispose();
        throw new Error('The scenario shell was disposed while starting.');
      }

      session = startedSession;
      sessionCwd = cwd;
    }

    ensureNotDisposed();
    const requestedArtifactName = options.artifactName || description;
    const artifactName = getArtifactName(requestedArtifactName, artifactCounts);
    const logPath = path.join(context.diagnosticsDir, `${artifactName}.log`);
    const startedRun = await session.startCommand(
      buildAspireCommand(context.aspireCommand, args),
      logPath);
    if (disposed) {
      startedRun.flushArtifacts();
      await session.dispose();
      throw new Error('The scenario shell was disposed while starting an Aspire command.');
    }

    activeRun = startedRun;
  }

  function waitFor(text, waitDescription, waitTimeoutMs = timeoutMs) {
    return getActiveRun().waitForText(text, waitTimeoutMs, waitDescription);
  }

  function waitForAny(texts, waitDescription, waitTimeoutMs = timeoutMs) {
    return getActiveRun().waitForAnyText(texts, waitTimeoutMs, waitDescription);
  }

  function type(text) {
    return getActiveRun().type(text);
  }

  function enter() {
    return getActiveRun().enter();
  }

  function ctrlC() {
    return getActiveRun().ctrlC();
  }

  function getCommandExitNeedle() {
    return getActiveRun().exitNeedle;
  }

  function getActiveRun() {
    if (!activeRun) {
      throw new Error('Start an Aspire command before interacting with the shell.');
    }

    return activeRun;
  }

  function ensureNotDisposed() {
    if (disposed) {
      throw new Error('The scenario shell has already been disposed.');
    }
  }

  async function dispose() {
    if (disposed) {
      return;
    }

    disposed = true;
    activeRun?.flushArtifacts();
    await session?.dispose();
  }

  return {
    ...context,
    ctrlC,
    enter,
    getCommandExitNeedle,
    runAspireCommand,
    type,
    waitFor,
    waitForAny,
    complete: completeActiveRun,
    dispose
  };
}

async function runCallback(callback, controller) {
  await callback(controller);
  await controller.complete();
}

function validateScenario(scenario) {
  if (!scenario || typeof scenario.description !== 'string' || scenario.description.length === 0) {
    throw new Error('A scenario must provide a description.');
  }

  if (typeof scenario.callback !== 'function') {
    throw new Error(`Scenario '${scenario.description}' must provide a callback.`);
  }

  if (scenario.cleanup !== undefined && typeof scenario.cleanup !== 'function') {
    throw new Error(`Scenario '${scenario.description}' must provide cleanup as a function.`);
  }

  if (scenario.timeoutMs !== undefined &&
      (!Number.isFinite(scenario.timeoutMs) || scenario.timeoutMs <= 0)) {
    throw new Error(`Scenario '${scenario.description}' must provide a positive timeout.`);
  }
}

function getArtifactName(name, artifactCounts) {
  const baseName = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '') || 'scenario';
  const count = (artifactCounts.get(baseName) || 0) + 1;
  artifactCounts.set(baseName, count);
  return count === 1 ? baseName : `${baseName}-${count}`;
}

async function withTimeout(promise, timeoutMs) {
  let timer = null;

  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error(`Timed out after ${Math.round(timeoutMs / 1000)} second(s).`)),
          timeoutMs);
      })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function waitForSettlement(promise, timeoutMs) {
  let timer = null;

  try {
    await Promise.race([
      promise.catch(() => {}),
      new Promise(resolve => {
        timer = setTimeout(resolve, timeoutMs);
      })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

module.exports = {
  runScenario
};
