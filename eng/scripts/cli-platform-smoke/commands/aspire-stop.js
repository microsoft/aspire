'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireStop({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-stop.log',
    command: buildAspireCommand(aspireCommand, ['stop']),
    timeoutMs,
    interact: async run => {
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
