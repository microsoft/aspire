'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireRunInteractive({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-run.log',
    timeoutMs,
    body: async runAspireCommand => {
      const run = await runAspireCommand(['run']);

      await run.waitForText('Press CTRL+C to stop the AppHost and exit.', timeoutMs, 'run ready banner');
      await run.ctrlC();
    }
  });
}

module.exports = {
  runAspireRunInteractive
};
