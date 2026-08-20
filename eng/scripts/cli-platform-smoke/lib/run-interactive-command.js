'use strict';

const path = require('path');

const { buildAspireCommand } = require('./aspire-command');
const { ShellSession } = require('./shell-session');

async function runInteractiveCommand({
  aspireCommand,
  cwd,
  diagnosticsDir,
  fileName,
  timeoutMs,
  body
}) {
  const logPath = path.join(diagnosticsDir, fileName);
  const session = await ShellSession.start(cwd);
  let run = null;

  try {
    await body(async args => {
      if (run) {
        throw new Error('This interactive command already started an Aspire process.');
      }

      run = await session.startCommand(buildAspireCommand(aspireCommand, args), logPath);
      return run;
    });

    if (!run) {
      throw new Error('The interactive command did not start an Aspire process.');
    }

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
