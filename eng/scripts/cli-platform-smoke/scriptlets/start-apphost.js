'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function startAppHost({
  aspireCommand,
  diagnosticsDir,
  maxStartupSeconds,
  projectRoot
}) {
  const timeoutMs = maxStartupSeconds * 1000 + 180_000;

  await runInteractiveCommand({
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'setup-aspire-start.log',
    command: buildAspireCommand(aspireCommand, ['start']),
    timeoutMs,
    interact: async run => {
      await run.waitForText('AppHost started successfully.', timeoutMs, 'setup AppHost start success');
    }
  });
}

module.exports = {
  startAppHost
};
