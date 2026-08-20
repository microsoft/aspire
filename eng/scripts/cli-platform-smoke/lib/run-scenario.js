'use strict';

const path = require('path');

const { buildAspireCommand } = require('./aspire-command');
const { ShellSession } = require('./shell-session');

const defaultTimeoutMs = 600_000;
const cleanupGracePeriodMs = 130_000;

async function runScenario(scenario, context) {
  const state = {
    artifactCounts: new Map(),
    cancelled: false,
    sessions: new Set()
  };

  try {
    await executeScenario(scenario, context, state);
  } catch (error) {
    state.cancelled = true;
    await disposeSessions(state);
    throw error;
  }
}

async function executeScenario(scenario, context, state, runAfterCancellation = false) {
  validateScenario(scenario);

  const timeoutMs = scenario.timeoutMs || defaultTimeoutMs;
  const artifactName = getArtifactName(scenario.description, state.artifactCounts);
  const cwd = scenario.cwd ? scenario.cwd(context) : context.projectRoot;
  let activeRun = null;
  let commandNumber = 0;
  let session = null;

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

  async function runAspireCommand(args) {
    if (state.cancelled && !runAfterCancellation) {
      throw new Error('The parent scenario has already ended.');
    }

    await completeActiveRun();
    if (!session) {
      session = await ShellSession.start(cwd);
      state.sessions.add(session);
    }

    commandNumber++;

    const commandArtifactName = commandNumber === 1
      ? artifactName
      : `${artifactName}-${commandNumber}`;
    const logPath = path.join(context.diagnosticsDir, `${commandArtifactName}.log`);
    activeRun = await session.startCommand(buildAspireCommand(context.aspireCommand, args), logPath);
    return activeRun;
  }

  async function runNestedScenario(nestedScenario) {
    const nestedRunAfterCancellation = nestedScenario.runAfterCancellation === true;
    if (state.cancelled && !nestedRunAfterCancellation) {
      throw new Error('The parent scenario has already ended.');
    }

    await completeActiveRun();
    await executeScenario(nestedScenario, context, state, nestedRunAfterCancellation);
  }

  const execution = (async () => {
    await scenario.callback({
      ...context,
      runAspireCommand,
      runScenario: runNestedScenario,
      timeoutMs
    });
    await completeActiveRun();
  })();

  try {
    await withTimeout(execution, timeoutMs);
  } catch (error) {
    if (error instanceof ScenarioTimeoutError) {
      state.cancelled = true;
      await disposeSessions(state);
      await waitForSettlement(execution, cleanupGracePeriodMs);
      await disposeSessions(state);
    }

    activeRun?.flushArtifacts();
    throw new Error(`${scenario.description}: ${error.message}`, { cause: error });
  } finally {
    await session?.dispose();
    state.sessions.delete(session);
  }
}

function validateScenario(scenario) {
  if (!scenario || typeof scenario.description !== 'string' || scenario.description.length === 0) {
    throw new Error('A scenario must provide a description.');
  }

  if (typeof scenario.callback !== 'function') {
    throw new Error(`Scenario '${scenario.description}' must provide a callback.`);
  }

  if (scenario.timeoutMs !== undefined &&
      (!Number.isFinite(scenario.timeoutMs) || scenario.timeoutMs <= 0)) {
    throw new Error(`Scenario '${scenario.description}' must provide a positive timeout.`);
  }

  if (scenario.cwd !== undefined && typeof scenario.cwd !== 'function') {
    throw new Error(`Scenario '${scenario.description}' must provide cwd as a function.`);
  }

  if (scenario.runAfterCancellation !== undefined &&
      typeof scenario.runAfterCancellation !== 'boolean') {
    throw new Error(
      `Scenario '${scenario.description}' must provide runAfterCancellation as a boolean.`);
  }
}

function getArtifactName(description, artifactCounts) {
  const baseName = description
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
          () => reject(new ScenarioTimeoutError(timeoutMs)),
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

async function disposeSessions(state) {
  await Promise.allSettled([...state.sessions].map(session => session.dispose()));
}

class ScenarioTimeoutError extends Error {
  constructor(timeoutMs) {
    super(`Timed out after ${Math.round(timeoutMs / 1000)} second(s).`);
  }
}

module.exports = {
  runScenario
};
