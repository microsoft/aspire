'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireStart({
  aspireCommand,
  cwd,
  diagnosticsDir,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-start.log',
    command: buildAspireCommand(aspireCommand, ['start']),
    timeoutMs,
    interact: async run => {
      await run.waitForText('AppHost started successfully.', timeoutMs, 'AppHost start success');
    }
  });
}

module.exports = {
  runAspireStart
};
