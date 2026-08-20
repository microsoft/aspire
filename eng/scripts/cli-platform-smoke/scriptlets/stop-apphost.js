'use strict';

const fs = require('fs');

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function stopAppHost({
  aspireCommand,
  diagnosticsDir,
  projectRoot
}) {
  if (!fs.existsSync(projectRoot)) {
    return;
  }

  try {
    await runInteractiveCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'cleanup-aspire-stop.log',
      command: buildAspireCommand(aspireCommand, ['stop']),
      timeoutMs: 120_000,
      interact: async run => {
        await run.waitForAnyText(
          ['Running instance stopped successfully.', 'No running AppHost found.'],
          120_000,
          'cleanup AppHost stop result');
      }
    });
  } catch {
    // Best-effort cleanup so a failed proof does not hide the original validation error.
  }
}

module.exports = {
  stopAppHost
};
