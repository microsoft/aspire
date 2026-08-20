'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireStart({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-start.log',
    timeoutMs,
    body: async runAspireCommand => {
      const run = await runAspireCommand(['start']);

      await run.waitForText('AppHost started successfully.', timeoutMs, 'AppHost start success');
    }
  });
}

module.exports = {
  runAspireStart
};
