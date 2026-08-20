'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireStop({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-stop.log',
    timeoutMs,
    body: async runAspireCommand => {
      const run = await runAspireCommand(['stop']);

      await run.waitForText(
        'Running instance stopped successfully.',
        timeoutMs,
        'AppHost stop success');
    }
  });
}

module.exports = {
  runAspireStop
};
