'use strict';

const fs = require('fs');

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
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'cleanup-aspire-stop.log',
      timeoutMs: 120_000,
      body: async runAspireCommand => {
        const run = await runAspireCommand(['stop']);

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
