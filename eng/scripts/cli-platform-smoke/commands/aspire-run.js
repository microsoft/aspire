'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireRunInteractive({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-run.log',
    command: buildAspireCommand(aspireCommand, ['run']),
    timeoutMs,
    interact: async run => {
      await run.waitForText('Press CTRL+C to stop the AppHost and exit.', timeoutMs, 'run ready banner');
      await run.ctrlC();
    }
  });
}

module.exports = {
  runAspireRunInteractive
};
