'use strict';

const path = require('path');

const { ShellSession } = require('./shell-session');

async function runInteractiveCommand({
  cwd,
  diagnosticsDir,
  fileName,
  command,
  timeoutMs,
  interact
}) {
  const logPath = path.join(diagnosticsDir, fileName);
  const session = await ShellSession.start(cwd);
  let run = null;

  try {
    run = await session.startCommand(command, logPath);
    await interact(run);
    await run.waitForExit(timeoutMs);
  } catch (error) {
    if (run && logPath) {
      run.flushArtifacts();
    }

    throw error;
  } finally {
    await session.dispose();
  }
}

module.exports = {
  runInteractiveCommand
};
